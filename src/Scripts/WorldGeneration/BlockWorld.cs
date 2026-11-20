using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Locally streams chunk meshes. Only the server edits voxels; the scene's
/// NetworkIdentity publishes only the world seed and identity. Edited
/// sections are requested on demand, paged, cached and kept current by deltas.
/// Generated chunk GameObjects are deliberately local/transient, never network objects.
/// </summary>
public class BlockWorld : Script
{
    public GameObject Player;
    public GameObject ViewCamera;

    public int Seed = 42;
    // Conservative default streaming radius; near chunks take priority before
    // distant scenery. These remain adjustable in the BlockWorld Inspector.
    public int RenderRadius = 6;
    public int SideRadius = 4;
    public int BehindRadius = 2;
    public int EditableRadius = 2;
    public int KeepMargin = 1;
    public int WorkerCount = 3;
    public int UploadsPerFrame = 3;
    public int VerticalRadius = 1;
    // Quadtree affects local rendering only; server-owned 16³ sections and
    // near-player collision are unchanged.
    public bool QuadtreeLodEnabled = true;
    public float LodQuality = 1f;
    // Drawing a distant mountain must not require distant chunk meshes to
    // render again in the cascaded sun-shadow passes.
    public bool LimitChunkShadows = true;
    public bool ChunkShadowsEnabled = true;
    public int ShadowRadius = 5;

    // Diagnostic only: synchronous mesh and collider entry-point timings.
    // Native Jolt cooking deferred to physics frames is NOT measured here.
    public bool ProfileChunkColliders = true;
    public int ProfileReportEvery = 32;
    public bool ProfileStreaming = true;
    public int StreamReportEvery = 64;
    public bool ProfileTerrainFrames = true;
    public int FrameReportEvery = 300;
    private int _frameSamples;
    private double _frameMsTotal;
    private double _frameMsMax;
    private double _generationMsTotal;
    private int _generationSamples;
    private int _streamUploads;
    private double _streamLatencyTotal;
    private double _streamLatencyMax;
    private readonly int[] _profileCounts = new int[4];
    private readonly double[] _profileMilliseconds = new double[4];
    private readonly double[] _profileMaximum = new double[4];
    private int _profileEvents;

    // 0: mesh upload, 1: MeshCollider add, 2: refresh, 3: remove.
    private void RecordChunkProfile(int operation, long start)
    {
        if (!ProfileChunkColliders || start == 0) return;
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _profileCounts[operation]++;
        _profileMilliseconds[operation] += elapsed;
        _profileMaximum[operation] = Math.Max(_profileMaximum[operation], elapsed);
        if (++_profileEvents < Math.Max(1, ProfileReportEvery)) return;

        string[] names = { "SetMesh", "ColliderAdd", "ColliderRefresh", "ColliderRemove" };
        Console.WriteLine("[Tallowcraft chunk profile] synchronous call timings (deferred Jolt work excluded):");
        for (int i = 0; i < 4; i++)
        {
            int count = _profileCounts[i];
            if (count > 0)
                Console.WriteLine($"  {names[i]} count={count} avg_ms={_profileMilliseconds[i] / count:F3} max_ms={_profileMaximum[i]:F3} total_ms={_profileMilliseconds[i]:F3}");
        }
        Array.Clear(_profileCounts);
        Array.Clear(_profileMilliseconds);
        Array.Clear(_profileMaximum);
        _profileEvents = 0;
    }

    private long ProfileStart() => ProfileChunkColliders ? Stopwatch.GetTimestamp() : 0;

    private void RecordStreamLatency(ChunkJob job)
    {
        if (!ProfileStreaming) return;
        double elapsed = Stopwatch.GetElapsedTime(job.EnqueuedAt).TotalMilliseconds;
        _streamLatencyTotal += elapsed;
        _generationMsTotal += job.Task.Result.GenerationMilliseconds;
        _generationSamples++;
        _streamLatencyMax = Math.Max(_streamLatencyMax, elapsed);
        if (++_streamUploads < Math.Max(1, StreamReportEvery)) return;
        Console.WriteLine(
            $"[Tallowcraft stream profile] uploads={_streamUploads} " +
            $"queue_to_upload_avg_ms={_streamLatencyTotal / _streamUploads:F1} " +
            $"max_ms={_streamLatencyMax:F1} " +
            $"live_meshes={_loaded.Count} empty_sections={_emptyChunks.Count} " +
            $"colliders={_colliders.Count}");
        _streamUploads = 0;
        _streamLatencyTotal = 0;
        _streamLatencyMax = 0;
    }

    // Metadata only. No voxel list is ever sent in a [Sync] field.
    // "3|seed|worldId" arrives automatically for late joiners.
    [Sync] public string EditState = "";

    private BlockEdits _edits = new();
    private WorldChestStorage _chests = new();
    private string _worldId = "";
    private bool _saveLoadFailed;
    private Task? _saveTask;
    private float _saveClock;
    private int _generation;
    private int _savedGeneration;
    private int _savingGeneration;

    private readonly HashSet<ChunkKey> _clientReady = new();
    private readonly Dictionary<ChunkKey, EditSectionTransfer> _transfers = new();
    private readonly List<ChunkKey> _transferScratch = new();
    private readonly Dictionary<ChunkKey, (int Revision, byte[] Bytes, string Hash)>
        _encodedSections = new();
    private string SaveFolder => Path.Combine(
        GamePaths.PlayerData, "tallowcraft", "worlds", "default");

    // Server-only stable identity for player saves. Transient client ids are
    // never used as inventory filenames: they change when reconnecting.
    public string WorldIdentity => Scene.IsServer ? _worldId : "";
    // The synchronized world identity is safe for local navigation-file
    // namespacing. It grants no authority to access server player saves.
    public string ClientWorldIdentity => _worldId;
    // A durable incomplete pickup receipt blocks all further gameplay
    // inventory mutations until startup can replay the transaction.
    public bool PersistenceLocked => _saveLoadFailed;
    public string PlayerProfilesFolder =>
        Path.Combine(SaveFolder, "players", _worldId);
    private string CacheFolder => Path.Combine(
        GamePaths.PlayerData, "tallowcraft", "chunk-cache");
    private readonly Dictionary<ChunkKey, LiveChunk> _loaded = new();
    private readonly Dictionary<ChunkKey, ChunkJob> _jobs = new();
    private TerrainSelection _distantSelection = new();
    private readonly Dictionary<TerrainTileKey, (GameObject Object,
        MeshRenderer Renderer, int Version, int Vertices, int Triangles)> _distantLoaded = new();
    private readonly Dictionary<TerrainTileKey, (Task<ChunkMesh> Task,
        CancellationTokenSource Cancel, int Version, long Queued)> _distantJobs = new();
    private readonly Dictionary<TerrainTileKey, int> _distantVersions = new();
    private readonly List<TerrainTileKey> _distantQueue = new();
    private readonly List<TerrainTileKey> _distantScratch = new();
    private bool _distantQueueDirty = true;
    private bool _distantNeedsRetire = true;
    private int _nextDistant;
    private int _distantUploads;
    private double _distantQueueLatency;
    private double _distantMaxLatency;
    // Successful zero-surface jobs do not need a GameObject or GPU buffers.
    private readonly Dictionary<ChunkKey, int> _emptyChunks = new();
    private readonly Dictionary<ChunkKey, int> _versions = new();
    private readonly Dictionary<ulong, ChunkKey> _chunkObjects = new();
    private readonly HashSet<ChunkKey> _colliders = new();
    private readonly HashSet<ChunkKey> _shadowCasters = new();
    private readonly List<ChunkKey> _scratch = new();
    // Reused candidate buffer. Find priorities cheaply first, then only
    // evaluate costly biome/tree/structure existence for slots we can queue.
    private readonly List<(ChunkKey Key, long Distance)> _candidates = new();
    private readonly HashSet<ChunkKey> _candidateKeys = new();
    // Reuse the sorted streaming queue across frames. Rebuild only when the
    // viewer/collision footprint or edited voxel state actually changes.
    private int _nextCandidate;
    private bool _candidateListDirty = true;
    private readonly List<ChunkKey> _playerCenters = new();
    private readonly List<float> _playerHeights = new();
    // Y=0 air edits expose the negative-Y layer from the surface.
    private readonly HashSet<ChunkKey> _exposedBelow = new();
    private float _viewerWorldY;
    private readonly CancellationTokenSource _stop = new();

    private Vector2 _lastForward = Vector2.UnitY; // 2D Y represents world Z.
    private Vector2 _lastStreamForward;
    private ChunkKey _lastStreamCenter;
    private readonly List<ChunkKey> _previousPlayerCenters = new();
    private readonly List<bool> _previousPlayerBelowSurface = new();
    private bool _streamStateKnown;
    private bool _streamExhausted;
    private bool _collisionRescan;
    private bool _lastViewerBelowSurface;
    private int _lastStreamSettings;
    private string _appliedSnapshot = "";
    private string _rejectedSnapshot = "";
    private int _publishedSeed;

    public override void Start()
    {
        if (!Scene.IsServer)
            return;

        if (WorldEditSave.HasSave(SaveFolder))
        {
            if (!WorldEditSave.TryLoad(SaveFolder, out string loadedId,
                    out int loadedSeed, out BlockEdits loaded,
                    out WorldChestStorage chests,
                    out IReadOnlyList<SavedWorldDrop> drops, out bool recovered))
            {
                _saveLoadFailed = true;
                Console.WriteLine("Tallowcraft world save is corrupt or incomplete; " +
                    "refusing to replace it with an empty world.");
                return;
            }
            _worldId = loadedId;
            Seed = loadedSeed;
            _edits = loaded;
            _chests = chests;
            try
            {
                // If a pickup crashed between writing the world and player
                // profile, finish both writes before exposing world objects.
                if (PickupTransactionStore.Recover(SaveFolder, _worldId,
                        Seed, _edits.Snapshot, _edits.Revisions,
                        _chests.Snapshot, out var recoveredDrops,
                        out var recoveredChests, out BlockEdits recoveredEdits))
                {
                    drops = recoveredDrops;
                    _edits = recoveredEdits;
                    _chests.Restore(recoveredChests);
                    Console.WriteLine("Tallowcraft completed an interrupted inventory transfer.");
                }
            }
            catch (Exception exception)
            {
                _saveLoadFailed = true;
                Console.WriteLine(
                    $"Tallowcraft pickup recovery failed; world locked: {exception}");
                return;
            }
            if (!WorldDrop.Restore(Scene, drops))
            {
                _saveLoadFailed = true;
                Console.WriteLine("Tallowcraft could not restore saved world drops; " +
                    "refusing to continue or overwrite this world save.");
                return;
            }
            RebuildSurfaceOpenings();
            if (recovered)
                Console.WriteLine(
                    "Tallowcraft recovered the previous world save after " +
                    "the latest generation failed validation.");
            Console.WriteLine($"Tallowcraft restored {_edits.Count} modified voxels.");
        }
        else
        {
            _worldId = WorldEditSave.NewWorldId();
            try
            {
                WorldEditSave.Save(SaveFolder, _worldId, Seed,
                    _edits.Snapshot, _edits.Revisions, _chests.Snapshot,
                    WorldDrop.Capture(Scene));
            }
            catch (Exception exception)
            {
                _saveLoadFailed = true;
                Console.WriteLine($"Unable to initialize world save: {exception}");
                return;
            }
        }
        _publishedSeed = Seed;
        EditState = WorldMetadata();
    }

    private string WorldMetadata() =>
        "3|" + Seed.ToString(CultureInfo.InvariantCulture) + "|" + _worldId;

    private void ApplyWorldMetadata()
    {
        if (EditState == _appliedSnapshot || EditState == _rejectedSnapshot)
            return;
        string[] pieces = EditState.Split('|');
        if (pieces.Length != 3 || pieces[0] != "3" ||
            !int.TryParse(pieces[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int seed) ||
            !Guid.TryParseExact(pieces[2], "N", out _))
        {
            _rejectedSnapshot = EditState;
            Console.WriteLine("Rejected invalid Tallowcraft world metadata.");
            return;
        }

        if (_worldId != pieces[2] || Seed != seed)
        {
            _edits = new BlockEdits();
            ResetDistantTerrain();
            _clientReady.Clear();
            _transfers.Clear();
            Seed = seed;
            _worldId = pieces[2];
            RebuildSurfaceOpenings();
            MarkAllDirty();
        }
        _appliedSnapshot = EditState;
    }

    /// <summary>One durable cross-file commit shared by ground pickup
    /// and player/chest transfers. A receipt is written before either file,
    /// and startup replays it if any subsequent step fails or is interrupted.
    /// The receipt and world checkpoint represent a single game-thread tick.</summary>
    private bool CommitInventoryWorld(PickupReceipt receipt,
        IReadOnlyDictionary<BlockPosition, ChestContents> afterChests,
        SectionedEditSnapshot? beforeMinedEdit = null,
        IReadOnlyDictionary<ChunkKey, int>? beforeMinedRevisions = null)
    {
        if (!Scene.IsServer || _saveLoadFailed ||
            receipt.WorldId != _worldId ||
            !PlayerIdentity.Valid(receipt.PlayerToken))
            return false;
        try
        {
            // Do not let an old asynchronous snapshot land after this commit.
            if (_saveTask != null)
            {
                _saveTask.GetAwaiter().GetResult();
                _saveTask = null;
                _savedGeneration = _savingGeneration;
            }
            if (beforeMinedEdit != null)
            {
                // The mined voxel is already changed in live ECS state but
                // must NOT reach disk before the journal exists. Checkpoint
                // the immutable snapshot captured just before that edit,
                // preserving all unrelated edits and their revisions.
                if (beforeMinedRevisions == null)
                    throw new InvalidOperationException(
                        "Mining transaction has no pre-edit revisions.");
                WorldEditSave.Save(SaveFolder, _worldId, Seed,
                    beforeMinedEdit, beforeMinedRevisions, _chests.Snapshot,
                    WorldDrop.Capture(Scene));
            }
            else if (_generation != _savedGeneration)
            {
                WorldEditSave.Save(SaveFolder, _worldId, Seed,
                    _edits.Snapshot, _edits.Revisions, _chests.Snapshot,
                    WorldDrop.Capture(Scene));
                _savedGeneration = _generation;
            }

            PickupTransactionStore.Prepare(SaveFolder, receipt);
            WorldEditSave.Save(SaveFolder, _worldId, Seed,
                _edits.Snapshot, _edits.Revisions,
                afterChests, receipt.Drops);
            PlayerProfileStore.Save(PlayerProfilesFolder, _worldId,
                receipt.PlayerToken, receipt.Inventory, receipt.Tools,
                receipt.SelectedType, receipt.SelectedShape, receipt.Facing,
                receipt.SelectedSlot);
            PickupTransactionStore.Complete(SaveFolder);
            _savedGeneration = _generation;
            _saveClock = 0f;
            return true;
        }
        catch (Exception exception)
        {
            if (PickupTransactionStore.HasPending(SaveFolder))
            {
                // A partial cross-file commit must never be overwritten.
                _saveLoadFailed = true;
                Console.WriteLine("Tallowcraft inventory transaction pending; " +
                    "world writes are locked until next-start recovery.");
            }
            Console.WriteLine($"Tallowcraft inventory transaction failed: {exception}");
            return false;
        }
    }

    /// <summary>A physical item may be removed only after its receiving
    /// player profile and the remaining drops have been committed to disk.</summary>
    public bool TryCommitDropPickup(WorldDrop drop, string token,
        BlockInventory inventoryAfter, PlayerTools tools,
        int selectedType, int selectedShape, int facing, int selectedSlot)
    {
        if (!Scene.IsServer || _saveLoadFailed ||
            !drop.GameObject.IsValid || drop.Scene.SessionId != Scene.SessionId)
            return false;
        var receipt = new PickupReceipt(_worldId, token,
            inventoryAfter, tools, selectedType, selectedShape,
            facing, selectedSlot, WorldDrop.Capture(Scene, drop));
        return CommitInventoryWorld(receipt, _chests.Snapshot);
    }

    /// <summary>Direct reward when the server cannot spawn a drop (for
    /// example at the 256-entity limit). The pre-edit snapshot is saved
    /// first, then the journal atomically couples the mined voxel to the
    /// post-credit player profile. Crash recovery can redo the missing edit.</summary>
    public bool TryCommitMiningCredit(BlockPosition position,
        ushort previousState, SectionedEditSnapshot beforeEdit,
        IReadOnlyDictionary<ChunkKey, int> beforeRevisions,
        string token, BlockInventory inventoryAfter, PlayerTools tools,
        int selectedType, int selectedShape, int facing, int selectedSlot)
    {
        if (!Scene.IsServer || _saveLoadFailed ||
            previousState == 0 || !BlockState.IsValid(previousState) ||
            !BlockEdits.ValidPosition(position) ||
            BlockAt(position) != 0)
            return false;

        var receipt = new PickupReceipt(_worldId, token,
            inventoryAfter, tools, selectedType, selectedShape, facing,
            selectedSlot, WorldDrop.Capture(Scene), EditedBlock: position,
            BlockBefore: previousState, BlockAfter: 0);
        return CommitInventoryWorld(receipt, _chests.Snapshot,
            beforeEdit, beforeRevisions);
    }

    /// <summary>Do not persist an edited voxel if an unexpected failure
    /// prevented both its item spawn and its inverse edit. Reopen the
    /// last committed world generation and report the failure.</summary>
    public void HaltAfterRewardFailure()
    {
        if (!Scene.IsServer) return;
        _saveLoadFailed = true;
        Console.WriteLine("Tallowcraft mining reward rollback failed; " +
            "world writes halted to preserve committed items.");
    }

    /// <summary>When a drop failed to spawn and the player cannot accept
    /// its fallback reward, return the mined block to the same scene tick.
    /// A pending receipt instead locks all gameplay until recovery.</summary>
    public bool TryUndoFailedMining(BlockPosition position,
        ushort previousState)
    {
        if (!Scene.IsServer || _saveLoadFailed ||
            previousState == 0 || !BlockState.IsValid(previousState) ||
            BlockAt(position) != 0 ||
            !_edits.Set(position, previousState, Seed))
            return false;
        MarkBlockDirty(position);
        UpdateSurfaceOpening(position);
        _generation++;
        ChunkKey section = SectionedEditSnapshot.SectionOf(position);
        _encodedSections.Remove(section);
        Rpc(nameof(ReceiveSectionDelta), section.X, section.Y, section.Z,
            _edits.Revision(section), (int)EditSectionCodec.Index(position),
            (int)previousState);
        return true;
    }

    /// <summary>Stage both inventories in memory, then atomically journal
    /// the player's post-transfer profile and only the affected chest slot.
    /// A failed disk write cannot modify either live inventory.</summary>
    public bool TryCommitChestTransfer(BlockPosition position,
        BlockInventory player, int playerSlot, int chestSlot,
        bool fromPlayer, bool split, string token, PlayerTools tools,
        int selectedType, int selectedShape, int facing, int selectedSlot,
        out BlockInventory inventoryAfter)
    {
        inventoryAfter = new BlockInventory();
        if (!TryReadChest(position, out _) ||
            !inventoryAfter.TryLoad(player.Serialize()))
            return false;

        var stagedStorage = new WorldChestStorage();
        stagedStorage.Restore(_chests.Snapshot);
        if (!stagedStorage.TryTransfer(position, inventoryAfter, playerSlot,
                chestSlot, fromPlayer, split))
            return false;

        var receipt = new PickupReceipt(_worldId, token,
            inventoryAfter, tools, selectedType, selectedShape, facing,
            selectedSlot, WorldDrop.Capture(Scene), position,
            stagedStorage.At(position));
        if (!CommitInventoryWorld(receipt, stagedStorage.Snapshot))
            return false;

        _chests.Restore(stagedStorage.Snapshot);
        _generation++;
        _savedGeneration = _generation;
        return true;
    }

    // Server-owned spawn, merge, pickup and despawn events mark saves dirty.
    // Aging/movement is also captured at intervals while live drops exist.
    public void MarkDropsChanged()
    {
        if (Scene.IsServer && !_saveLoadFailed) _generation++;
    }

    private void TickSaves(float deltaTime)
    {
        if (_saveTask != null && _saveTask.IsCompleted)
        {
            if (_saveTask.IsFaulted)
                Console.WriteLine($"Tallowcraft save failed: {_saveTask.Exception}");
            else
                _savedGeneration = _savingGeneration;
            _saveTask = null;
        }
        _saveClock += Math.Max(0f, deltaTime);
        if (_saveTask != null || _saveClock < 30f ||
            (_generation == _savedGeneration && !WorldDrop.HasLive(Scene)))
            return;
        _saveClock = 0;
        var snapshot = _edits.Snapshot;
        var revisions = _edits.Revisions;
        var chests = _chests.Snapshot;
        // Capture physics positions on the game thread before saving.
        IReadOnlyList<SavedWorldDrop> drops = WorldDrop.Capture(Scene);
        string folder = SaveFolder, id = _worldId;
        int seed = Seed;
        _savingGeneration = _generation;
        _saveTask = Task.Run(() =>
            WorldEditSave.Save(folder, id, seed, snapshot, revisions,
                chests, drops));
    }

    private void SaveNow()
    {
        if (_saveLoadFailed || _worldId.Length == 0)
            return;
        try
        {
            _saveTask?.GetAwaiter().GetResult();
            _saveTask = null;
            if (_generation != _savedGeneration || WorldDrop.HasLive(Scene))
            {
                WorldEditSave.Save(SaveFolder, _worldId, Seed,
                    _edits.Snapshot, _edits.Revisions, _chests.Snapshot,
                    WorldDrop.Capture(Scene));
                _savedGeneration = _generation;
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Tallowcraft save failed: {exception}");
        }
    }

    public override void OnSaving()
    {
        if (Scene.IsServer)
            SaveNow();
    }

    // Rendering, chunk jobs and synchronized snapshots are local per machine,
    // including clients that do not execute a server-owned NetworkIdentity's
    // networked Update callback. Never run streaming during prediction replay.
    public override void LocalUpdate(float deltaTime)
    {
        // A client must have the authoritative seed before generating any
        // chunks, then must validate each requested section before meshing.
        if (Scene.IsServer)
        {
            if (_saveLoadFailed)
                return;
            if (Seed != _publishedSeed)
            {
                // Complete any old async save before switching world identity
                // in the shared default save directory.
                try { _saveTask?.GetAwaiter().GetResult(); }
                catch (Exception e)
                {
                    Console.WriteLine($"Tallowcraft previous save failed: {e}");
                    _saveLoadFailed = true;
                    return;
                }
                _saveTask = null;
                WorldDrop.Clear(Scene);
                // A changed seed starts a completely new world; prior edits
                // cannot be applied against different procedural terrain.
                _publishedSeed = Seed;
                _worldId = WorldEditSave.NewWorldId();
                _edits = new BlockEdits();
                _chests = new WorldChestStorage();
                _encodedSections.Clear();
                ResetDistantTerrain();
                _generation++;
                EditState = WorldMetadata();
                _exposedBelow.Clear();
                MarkAllDirty();
            }
            TickSaves(deltaTime);
        }
        else
        {
            if (EditState.Length == 0)
                return;
            ApplyWorldMetadata();
            if (_appliedSnapshot != EditState)
                return;
        }

        GameObject anchor = Viewer();
        GameObject view = ViewCamera.IsValid ? ViewCamera : Scene.Find("Main Camera") ?? anchor;
        Vector3 position = anchor.IsValid ? anchor.Transform.Position : Vector3.Zero;
        Vector3 forward = view.IsValid ? view.Transform.Forward : Vector3.UnitZ;
        Vector2 flat = new(forward.X, forward.Z);

        if (flat.LengthSquared() > 0.0001f)
            _lastForward = Vector2.Normalize(flat);

        int cy = Math.Clamp(ChunkGenerator.FloorDiv((int)MathF.Floor(position.Y), ChunkGenerator.Size),
            ChunkGenerator.MinChunkY, ChunkGenerator.MaxChunkY);
        _viewerWorldY = position.Y;
        int cx = ChunkGenerator.FloorDiv((int)MathF.Floor(position.X), ChunkGenerator.Size);
        int cz = ChunkGenerator.FloorDiv((int)MathF.Floor(position.Z), ChunkGenerator.Size);

        GatherCollisionCenters(cx, cy, cz);
        bool streamChanged = StreamStateChanged(cx, cy, cz);
        if (streamChanged)
        {
            RebuildDistantSelection(cx, cz);
            // These scans are proportional to the live chunk count. They
            // belong to stream boundary changes, not every rendered frame.
            CancelObsoleteJobs(cx, cy, cz);
            UnloadDistant(cx, cy, cz);
            if (!Scene.IsServer)
                DropDistantTransfers(cx, cy, cz);
        }
        int uploaded = UploadFinished(cx, cy, cz);
        if (uploaded > 0) _distantNeedsRetire = true;
        if (_collisionRescan)
        {
            // Turning changes directional mesh residency, not who needs
            // colliders. Avoid full live-chunk scans on camera rotation.
            UpdateColliders(cx, cy, cz);
            MaintainBlockCache(cx, cy, cz);
        }
        if (!_streamExhausted)
            StartNearestJobs(cx, cy, cz);
        TickDistantTerrain(cx, cy, cz,
            Math.Max(0, Math.Clamp(UploadsPerFrame, 1, 4) - uploaded));
        if (!Scene.IsServer)
            RequestWaitingSections(deltaTime);
        RecordFrameDiagnostics(deltaTime);
    }

    private void RecordFrameDiagnostics(float deltaTime)
    {
        if (!ProfileTerrainFrames) return;
        double ms = Math.Clamp(deltaTime * 1000.0, 0.0, 1000.0);
        _frameMsTotal += ms;
        _frameMsMax = Math.Max(_frameMsMax, ms);
        if (++_frameSamples < Math.Max(30, FrameReportEvery)) return;
        long fullVerts = 0, fullTris = 0, lodVerts = 0, lodTris = 0;
        foreach (LiveChunk chunk in _loaded.Values)
        {
            fullVerts += chunk.VertexCount;
            fullTris += chunk.TriangleCount;
        }
        foreach (var tile in _distantLoaded.Values)
        {
            lodVerts += tile.Vertices;
            lodTris += tile.Triangles;
        }
        // Approximate CPU mesh-array footprint; excludes GPU driver allocation
        // and MeshRenderer-native bookkeeping. WorkingSet is whole process RSS.
        long meshBytes = (fullVerts + lodVerts) * 32L +
            (fullTris + lodTris) * 12L;
        long workingSet = 0;
        try
        {
            using Process process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
        }
        catch (Exception) { } // Some sandboxed platforms hide process memory.
        double mean = _frameMsTotal / _frameSamples;
        Console.WriteLine(
            $"[Tallowcraft frame profile] ahead={RenderRadius} " +
            $"lod={QuadtreeLodEnabled} quality={LodQuality:F2} " +
            $"avg_fps={(mean > 0.001 ? 1000 / mean : 0):F1} " +
            $"avg_ms={mean:F2} max_ms={_frameMsMax:F2} " +
            $"full_chunks={_loaded.Count} lod_tiles={_distantLoaded.Count} " +
            $"full_verts={fullVerts} lod_verts={lodVerts} " +
            $"full_tris={fullTris} lod_tris={lodTris} " +
            $"mesh_array_bytes_est={meshBytes} process_rss_bytes={workingSet} " +
            $"near_jobs={_jobs.Count} far_jobs={_distantJobs.Count} " +
            $"avg_gen_ms={(_generationSamples == 0 ? 0 : _generationMsTotal / _generationSamples):F2} " +
            "draw_calls=unavailable");
        _frameSamples = 0;
        _frameMsTotal = _frameMsMax = 0;
        _generationMsTotal = 0;
        _generationSamples = 0;
    }

    private void DropDistantTransfers(int cx, int cy, int cz)
    {
        _transferScratch.Clear();
        foreach (var item in _transfers)
            if (!ShouldKeep(item.Key, cx, cy, cz, Math.Clamp(KeepMargin, 0, 3) + 1))
                _transferScratch.Add(item.Key);
        foreach (ChunkKey key in _transferScratch)
            _transfers.Remove(key);
    }

    private bool StreamStateChanged(int cx, int cy, int cz)
    {
        int settings = HashCode.Combine(RenderRadius, SideRadius, BehindRadius,
            EditableRadius, KeepMargin, VerticalRadius, ShadowRadius,
            HashCode.Combine(LimitChunkShadows, ChunkShadowsEnabled,
                QuadtreeLodEnabled, LodQuality));
        ChunkKey center = new(cx, cy, cz);
        bool belowSurface = _viewerWorldY < 5f;
        // Only rescan the directional mesh footprint when orientation changes
        // enough to add/remove a chunk. Small per-frame camera motion shouldn't
        // run thousands of live-chunk distance tests.
        bool collisionChanged = !_streamStateKnown ||
            _lastStreamCenter != center ||
            belowSurface != _lastViewerBelowSurface ||
            _lastStreamSettings != settings ||
            _previousPlayerCenters.Count != _playerCenters.Count;
        if (!collisionChanged)
            for (int i = 0; i < _playerCenters.Count; i++)
                if (_previousPlayerCenters[i] != _playerCenters[i] ||
                    _previousPlayerBelowSurface[i] != (_playerHeights[i] < 5f))
                {
                    collisionChanged = true;
                    break;
                }

        _collisionRescan = collisionChanged;
        bool changed = collisionChanged ||
            Vector2.Dot(_lastStreamForward, _lastForward) < 0.997f;
        if (!changed) return false;

        _streamStateKnown = true;
        _lastStreamCenter = center;
        _lastStreamForward = _lastForward;
        _lastViewerBelowSurface = belowSurface;
        _lastStreamSettings = settings;
        _previousPlayerCenters.Clear();
        _previousPlayerBelowSurface.Clear();
        for (int i = 0; i < _playerCenters.Count; i++)
        {
            _previousPlayerCenters.Add(_playerCenters[i]);
            _previousPlayerBelowSurface.Add(_playerHeights[i] < 5f);
        }
        _streamExhausted = false;
        _candidateListDirty = true;
        return true;
    }

    private GameObject Viewer()
    {
        // A client's camera must follow its OWN player, not an arbitrary scene object.
        if (Scene.IsNetworked &&
            Network.PlayerObject(Scene, Scene.LocalClientId) is { } local &&
            local.IsValid)
            return local;

        return Player.IsValid ? Player : ViewCamera.IsValid ? ViewCamera :
            Scene.Find("Main Camera") ?? GameObject;
    }

    private void GatherCollisionCenters(int cx, int cy, int cz)
    {
        _playerCenters.Clear();
        _playerHeights.Clear();

        if (Scene.IsServer && Scene.IsNetworked)
        {
            AddPlayerCenter(Network.PlayerObject(Scene, 0));
            foreach (int clientId in Network.ClientIds(Scene))
                AddPlayerCenter(Network.PlayerObject(Scene, clientId));
        }

        if (_playerCenters.Count == 0 || !Scene.IsServer)
        {
            _playerCenters.Add(new ChunkKey(cx, cy, cz));
            _playerHeights.Add(_viewerWorldY);
        }
    }

    private void AddPlayerCenter(GameObject? player)
    {
        if (player is not { } p || !p.IsValid)
            return;

        Vector3 position = p.Transform.Position;
        _playerHeights.Add(position.Y);
        _playerCenters.Add(new ChunkKey(
            ChunkGenerator.FloorDiv((int)MathF.Floor(position.X), ChunkGenerator.Size),
            Math.Clamp(ChunkGenerator.FloorDiv((int)MathF.Floor(position.Y), ChunkGenerator.Size),
                ChunkGenerator.MinChunkY, ChunkGenerator.MaxChunkY),
            ChunkGenerator.FloorDiv((int)MathF.Floor(position.Z), ChunkGenerator.Size)));
    }

    private static bool Near(ChunkKey key, int cx, int cy, int cz, int radius)
    {
        long dx = (long)key.X - cx, dz = (long)key.Z - cz;
        return Math.Abs((long)key.Y - cy) <= Math.Clamp(radius, 0, 2) &&
            dx * dx + dz * dz <= (long)radius * radius;
    }

    private bool HiddenForViewer(ChunkKey key) =>
        ChunkScheduling.HiddenBelowSurface(
            key, _viewerWorldY, _exposedBelow.Contains(key));

    private bool HiddenForPlayer(ChunkKey key, float height) =>
        ChunkScheduling.HiddenBelowSurface(
            key, height, _exposedBelow.Contains(key));

    /// <summary>Track surface shafts on edit changes, not every streaming frame.</summary>
    private void RebuildSurfaceOpenings()
    {
        _exposedBelow.Clear();
        foreach (var edit in _edits.Snapshot)
            if (edit.Key.Y == 0 && edit.Value == 0)
                _exposedBelow.Add(new ChunkKey(
                    ChunkGenerator.FloorDiv(edit.Key.X, ChunkGenerator.Size),
                    -1,
                    ChunkGenerator.FloorDiv(edit.Key.Z, ChunkGenerator.Size)));
    }

    // Only the one surface section changed on a block edit needs rescanning.
    private void UpdateSurfaceOpening(BlockPosition position)
    {
        if (position.Y != 0)
            return;
        ChunkKey below = new(
            ChunkGenerator.FloorDiv(position.X, ChunkGenerator.Size), -1,
            ChunkGenerator.FloorDiv(position.Z, ChunkGenerator.Size));
        ChunkKey surface = new(below.X, 0, below.Z);
        bool open = false;
        foreach (var edit in _edits.InSection(surface))
            if (edit.Key.Y == 0 && edit.Value == 0)
            {
                open = true;
                break;
            }
        if (open)
            _exposedBelow.Add(below);
        else
            _exposedBelow.Remove(below);
    }

    private int CollisionRadius() =>
        Math.Clamp(EditableRadius, 0, Math.Clamp(RenderRadius, 1, WorldDisplaySettings.MaxDistance));

    private bool ShouldStream(ChunkKey key, int cx, int cy, int cz, int extra = 0)
    {
        // The quadtree's LOD0 leaves own the full-resolution camera footprint.
        // Collider sections for remote players are scheduled separately.
        if (QuadtreeLodEnabled &&
            !_distantSelection.Full.Contains(new TerrainColumn(key.X, key.Z)))
            return false;
        if (key.Y < ChunkGenerator.MinChunkY || key.Y > ChunkGenerator.MaxChunkY ||
            Math.Abs((long)key.Y - cy) > Math.Clamp(VerticalRadius, 0, 2) + extra ||
            HiddenForViewer(key))
            return false;
        int forwardRange = Math.Clamp(RenderRadius, 1,
            QuadtreeLodEnabled ? WorldDisplaySettings.MaxDistance : 20);
        int sideRange = Math.Clamp(SideRadius, 1, forwardRange);
        int backRange = Math.Clamp(BehindRadius, 0, sideRange);
        int nearRange = QuadtreeLodEnabled
            ? Math.Clamp((int)MathF.Round(TerrainQuadtree.DefaultFullRadius *
                Math.Clamp(LodQuality, 0.65f, 1.6f)), 2, forwardRange)
            : CollisionRadius();

        float dx = key.X - cx, dz = key.Z - cz;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance <= nearRange + extra || distance < 0.0001f)
            return true;

        float facing = (dx * _lastForward.X + dz * _lastForward.Y) / distance;
        float allowed = facing >= 0
            ? sideRange + (forwardRange - sideRange) * facing
            : sideRange + (sideRange - backRange) * facing;

        return distance <= MathF.Max(allowed, nearRange) + extra;
    }

    private bool ShouldKeep(ChunkKey key, int cx, int cy, int cz, int margin = 0)
    {
        if (ShouldStream(key, cx, cy, cz, margin))
            return true;
        // Do not discard existing full-detail meshes before their coarse
        // replacement has completed its worker build and render upload.
        if (QuadtreeLodEnabled && _distantSelection.ContainsTile(key.X, key.Z) &&
            !DistantTileReady(key.X, key.Z))
            return true;

        // Dedicated/host server must simulate collision around every client,
        // even when that player is far outside the host's viewing direction.
        if (Scene.IsServer)
            for (int i = 0; i < _playerCenters.Count; i++)
            {
                ChunkKey center = _playerCenters[i];
                if (!HiddenForPlayer(key, _playerHeights[i]) &&
                    Near(key, center.X, center.Y, center.Z, CollisionRadius() + margin))
                    return true;
            }

        return false;
    }

    private bool ShouldCastChunkShadow(ChunkKey key, int cx, int cy, int cz)
    {
        if (!ChunkShadowsEnabled)
            return false;
        if (!LimitChunkShadows)
            return true;
        int radius = Math.Clamp(ShadowRadius, 0, Math.Clamp(RenderRadius, 1, WorldDisplaySettings.MaxDistance));
        return ChunkScheduling.ShouldCastShadow(key, cx, cy, cz, radius);
    }

    private bool ShouldHaveCollider(ChunkKey key, int cx, int cy, int cz, int margin = 0)
    {
        if (!HiddenForViewer(key) &&
            Near(key, cx, cy, cz, CollisionRadius() + margin))
            return true;
        if (Scene.IsServer)
            for (int i = 0; i < _playerCenters.Count; i++)
            {
                ChunkKey center = _playerCenters[i];
                if (!HiddenForPlayer(key, _playerHeights[i]) &&
                    Near(key, center.X, center.Y, center.Z, CollisionRadius() + margin))
                    return true;
            }
        return false;
    }

    private void CancelObsoleteJobs(int cx, int cy, int cz)
    {
        foreach (var pair in _jobs)
        {
            if (pair.Value.Task.IsCompleted ||
                (pair.Value.Version == VersionFor(pair.Key) &&
                 ShouldKeep(pair.Key, cx, cy, cz)))
                continue;
            pair.Value.Cancellation.Cancel();
        }
    }

    private void UnloadDistant(int cx, int cy, int cz)
    {
        _scratch.Clear();
        foreach (var pair in _loaded)
        {
            if (ShouldKeep(pair.Key, cx, cy, cz, Math.Clamp(KeepMargin, 0, 3)))
                continue;

            if (pair.Value.Object.IsValid)
            {
                pair.Value.Renderer.ClearMesh();
                pair.Value.Object.Destroy();
            }

            _chunkObjects.Remove(pair.Value.Object.Id);
            _colliders.Remove(pair.Key);
            _shadowCasters.Remove(pair.Key);
            _scratch.Add(pair.Key);
        }

        foreach (ChunkKey key in _scratch)
            _loaded.Remove(key);

        // Empty sections use metadata only; retain it while nearby so
        // returning to them doesn't repeat a zero-triangle generation job.
        _scratch.Clear();
        foreach (var pair in _emptyChunks)
            if (!ShouldKeep(pair.Key, cx, cy, cz,
                Math.Clamp(KeepMargin, 0, 3)))
                _scratch.Add(pair.Key);
        foreach (ChunkKey key in _scratch)
            _emptyChunks.Remove(key);
    }

    private void DecorateChestFronts(ChunkKey key, LiveChunk chunk,
        ushort[] blocks)
    {
        chunk.ClearDecorations();
        for (int y=0;y<16;y++)
        for (int z=0;z<16;z++)
        for (int x=0;x<16;x++)
        {
            BlockState state=new(blocks[ChunkGenerator.Index(x,y,z)]);
            if (state.Type != BlockInventory.Chest) continue;
            ChestFrontMesh.Build(state.Facing, out Vector3[] points,
                out Vector3[] normals, out Vector2[] uvs,
                out int[] indices, out Vector3 offset);

            GameObject front=Scene.CreateGameObject("Chest lock and lid");
            front.Transient=true;
            front.Transform.Position=new Vector3(
                key.X*16+x+.5f,key.Y*16+y+.5f,
                key.Z*16+z+.5f)+offset;
            var renderer=front.AddComponent<MeshRenderer>();
            renderer.CastShadows=false;
            renderer.FlatShading=true;
            renderer.BackFaceCulling=false;
            renderer.Material="Materials/chest_front.tmat";
            renderer.SetMesh(points,indices,normals,uvs);
            front.SetParent(chunk.Object,true);
            chunk.Decorations.Add(front);
        }
    }

    private int UploadFinished(int cx, int cy, int cz)
    {
        int processed = 0;
        _scratch.Clear();

        foreach (var pair in _jobs)
        {
            if (processed >= Math.Clamp(UploadsPerFrame, 1, 4))
                break;

            ChunkJob job = pair.Value;
            if (!job.Task.IsCompleted)
                continue;

            processed++;
            _scratch.Add(pair.Key);
            job.Cancellation.Dispose();

            if (job.Task.IsFaulted)
            {
                Console.WriteLine($"Chunk {pair.Key} failed: {job.Task.Exception}");
                // Do not continuously retry a failing chunk every frame:
                // the next footprint change or block edit will re-queue it.
                continue;
            }

            if (!job.Task.IsCompletedSuccessfully ||
                job.Version != VersionFor(pair.Key) ||
                !ShouldKeep(pair.Key, cx, cy, cz))
            {
                // A previously canceled job can be needed again if the
                // camera moves back before it finishes. Rebuild the queue.
                if (ShouldKeep(pair.Key, cx, cy, cz))
                {
                    // A stale result may be from a block edit while that
                    // key still occupied a worker slot. Requeue the current
                    // version after the obsolete task has been removed.
                    _candidateListDirty = true;
                    _streamExhausted = false;
                }
                continue;
            }

            ChunkMesh data = job.Task.Result;
            RecordStreamLatency(job);

            if (data.Triangles.Length == 0)
            {
                // No exposed faces means no visible mesh and no useful mesh
                // collider. Remember the empty generation to avoid repeating
                // it while this section stays in the stream footprint.
                _emptyChunks[pair.Key] = job.Version;
                if (_loaded.TryGetValue(pair.Key, out LiveChunk? old))
                {
                    if (old.Object.IsValid)
                    {
                        old.Renderer.ClearMesh();
                        old.Object.Destroy();
                    }
                    _chunkObjects.Remove(old.Object.Id);
                    _colliders.Remove(pair.Key);
                    _shadowCasters.Remove(pair.Key);
                    _loaded.Remove(pair.Key);
                }
                continue;
            }
            _emptyChunks.Remove(pair.Key);

            if (_loaded.TryGetValue(pair.Key, out LiveChunk? live))
            {
                if (!live.Object.IsValid)
                    continue;

                long meshStart = ProfileStart();
                live.Renderer.SetMesh(data.Vertices, data.Triangles, data.Normals, data.Uvs);
                DecorateChestFronts(pair.Key, live, data.Blocks);
                RecordChunkProfile(0, meshStart);
                if (_colliders.Contains(pair.Key) &&
                    live.Object.GetComponent<MeshCollider>() is { } collider &&
                    collider.IsValid)
                {
                    long refreshStart = ProfileStart();
                    collider.Refresh();
                    RecordChunkProfile(2, refreshStart);
                }

                live.Object.Entity.Set(new ChunkInfo
                {
                    X = pair.Key.X, Y = pair.Key.Y, Z = pair.Key.Z,
                    FaceCount = data.Triangles.Length / 6
                });
                live.Blocks = ShouldHaveCollider(pair.Key, cx, cy, cz) ? data.Blocks : null;
                live.VertexCount = data.Vertices.Length;
                live.TriangleCount = data.Triangles.Length / 3;
                live.Dirty = false;
                continue;
            }

            GameObject obj = Scene.CreateGameObject($"Chunk {pair.Key.X}, {pair.Key.Y}, {pair.Key.Z}");
            obj.Transient = true;
            obj.Transform.Position = new Vector3(
                pair.Key.X * ChunkGenerator.Size, pair.Key.Y * ChunkGenerator.Size,
                pair.Key.Z * ChunkGenerator.Size);

            MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
            renderer.FlatShading = true;
            renderer.Texture = BlockTextureAtlas.Path;
            bool castChunkShadow = ShouldCastChunkShadow(pair.Key, cx, cy, cz);
            renderer.CastShadows = castChunkShadow;
            if (castChunkShadow)
                _shadowCasters.Add(pair.Key);
            long newMeshStart = ProfileStart();
            renderer.SetMesh(data.Vertices, data.Triangles, data.Normals, data.Uvs);
            RecordChunkProfile(0, newMeshStart);

            bool collision = ShouldHaveCollider(pair.Key, cx, cy, cz);
            if (collision)
            {
                long colliderStart = ProfileStart();
                obj.AddComponent<MeshCollider>();
                RecordChunkProfile(1, colliderStart);
                _colliders.Add(pair.Key);
            }

            obj.Entity.Set(new ChunkInfo
            {
                X = pair.Key.X, Y = pair.Key.Y, Z = pair.Key.Z,
                FaceCount = data.Triangles.Length / 6
            });
            _chunkObjects[obj.Id] = pair.Key;
            var newChunk=new LiveChunk(obj,renderer,
                collision ? data.Blocks : null);
            newChunk.VertexCount = data.Vertices.Length;
            newChunk.TriangleCount = data.Triangles.Length / 3;
            _loaded.Add(pair.Key,newChunk);
            DecorateChestFronts(pair.Key,newChunk,data.Blocks);
        }

        foreach (ChunkKey key in _scratch)
            _jobs.Remove(key);
        return processed;
    }

    private void UpdateColliders(int cx, int cy, int cz)
    {
        foreach (var pair in _loaded)
        {
            ChunkKey key = pair.Key;
            GameObject obj = pair.Value.Object;
            if (!obj.IsValid)
                continue;

            bool shouldCastShadow = ShouldCastChunkShadow(key, cx, cy, cz);
            if (shouldCastShadow != _shadowCasters.Contains(key))
            {
                pair.Value.Renderer.CastShadows = shouldCastShadow;
                if (shouldCastShadow)
                    _shadowCasters.Add(key);
                else
                    _shadowCasters.Remove(key);
            }

            bool has = _colliders.Contains(key);
            if (!has && ShouldHaveCollider(key, cx, cy, cz))
            {
                long colliderStart = ProfileStart();
                obj.AddComponent<MeshCollider>();
                RecordChunkProfile(1, colliderStart);
                _colliders.Add(key);
            }
            else if (has && !ShouldHaveCollider(key, cx, cy, cz, 1))
            {
                long removeStart = ProfileStart();
                obj.RemoveComponent<MeshCollider>();
                RecordChunkProfile(3, removeStart);
                _colliders.Remove(key);
            }
        }
    }

    private void MaintainBlockCache(int cx, int cy, int cz)
    {
        var snapshot = _edits.Snapshot;
        foreach (var pair in _loaded)
        {
            if (ShouldHaveCollider(pair.Key, cx, cy, cz))
                pair.Value.Blocks ??=
                    ChunkGenerator.CreateBlocks(pair.Key, Seed, snapshot);
            else
                pair.Value.Blocks = null;
        }
    }

    private int VersionFor(ChunkKey key) =>
        _versions.TryGetValue(key, out int version) ? version : 0;

    private void MarkDirty(ChunkKey key)
    {
        _streamExhausted = false;
        _candidateListDirty = true;
        _emptyChunks.Remove(key);
        if (_jobs.TryGetValue(key, out ChunkJob? running))
            running.Cancellation.Cancel();
        _versions[key] = VersionFor(key) + 1;
        if (_loaded.TryGetValue(key, out LiveChunk? chunk))
            chunk.Dirty = true;
    }

    private void MarkAllDirty()
    {
        // Empty sections are generated from the old seed too. Their cache
        // must not survive a synchronized world-seed replacement.
        _emptyChunks.Clear();
        _candidateListDirty = true;
        _streamExhausted = false;
        foreach (ChunkKey key in _loaded.Keys)
            MarkDirty(key);
        foreach (ChunkKey key in _jobs.Keys)
            MarkDirty(key);
    }

    private void MarkBlockDirty(BlockPosition position)
    {
        foreach (TerrainTileKey tile in TerrainQuadtree.Ancestors(position.X, position.Z))
        {
            _distantVersions[tile] = DistantVersion(tile) + 1;
            if (_distantJobs.TryGetValue(tile, out var running))
                running.Cancel.Cancel();
        }
        _distantQueueDirty = true;
        int size = ChunkGenerator.Size;
        int x = ChunkGenerator.FloorDiv(position.X, size);
        int y = ChunkGenerator.FloorDiv(position.Y, size);
        int z = ChunkGenerator.FloorDiv(position.Z, size);
        int localX = position.X - x * size;
        int localY = position.Y - y * size;
        int localZ = position.Z - z * size;

        MarkDirty(new ChunkKey(x, y, z));
        if (localX == 0) MarkDirty(new ChunkKey(x - 1, y, z));
        if (localX == size - 1) MarkDirty(new ChunkKey(x + 1, y, z));
        if (localY == 0) MarkDirty(new ChunkKey(x, y - 1, z));
        if (localY == size - 1) MarkDirty(new ChunkKey(x, y + 1, z));
        if (localZ == 0) MarkDirty(new ChunkKey(x, y, z - 1));
        if (localZ == size - 1) MarkDirty(new ChunkKey(x, y, z + 1));
    }

    private bool EnsureSectionReady(ChunkKey key)
    {
        if (_clientReady.Contains(key))
            return true;
        if (_transfers.ContainsKey(key))
            return false;

        var transfer = new EditSectionTransfer(key);
        if (WorldEditCache.TryRead(CacheFolder, _worldId, key, Seed,
                out int revision, out string checksum, out var cached))
        {
            transfer.CacheRevision = revision;
            transfer.CacheHash = checksum;
            transfer.Cached = cached;
        }
        _transfers.Add(key, transfer);
        return false;
    }

    private void RequestWaitingSections(float deltaTime)
    {
        if (_transfers.Count == 0)
            return;
        GameObject player = Viewer();
        BlockEditor? editor = player.IsValid ? player.GetComponent<BlockEditor>() : null;
        if (editor == null)
            return;

        int sent = 0;
        foreach (var pair in _transfers)
        {
            // One per frame also respects the server's owner-RPC rate limit.
            if (sent >= 1)
                break;
            EditSectionTransfer transfer = pair.Value;
            transfer.RetrySeconds -= Math.Max(0f, deltaTime);
            if (transfer.RetrySeconds > 0f)
                continue;
            transfer.RetrySeconds = 0.6f;
            editor.RequestWorldSection(transfer.Key, transfer.NextPage,
                transfer.NextPage == 0 ? transfer.CacheRevision : -1,
                transfer.NextPage == 0 ? transfer.CacheHash : "");
            sent++;
        }
    }

    // A request arrives on the player's owned NetworkIdentity, which
    // authenticates RpcSender. Bound requests to the player's actual location.
    public void SendSectionPage(int clientId, ChunkKey key, int page,
        int clientRevision, string clientHash)
    {
        if (!Scene.IsServer || _saveLoadFailed ||
            key.Y < ChunkGenerator.MinChunkY || key.Y > ChunkGenerator.MaxChunkY ||
            page < 0 || page >= 64)
            return;

        GameObject? player = Network.PlayerObject(Scene, clientId);
        if (player is not { } p || !p.IsValid)
            return;
        Vector3 pos = p.Transform.Position;
        int centerX = ChunkGenerator.FloorDiv((int)MathF.Floor(pos.X), 16);
        int centerY = ChunkGenerator.FloorDiv((int)MathF.Floor(pos.Y), 16);
        int centerZ = ChunkGenerator.FloorDiv((int)MathF.Floor(pos.Z), 16);
        if (Math.Abs((long)key.X - centerX) > 18 ||
            Math.Abs((long)key.Z - centerZ) > 18 ||
            Math.Abs((long)key.Y - centerY) > 3)
            return;

        int revision = _edits.Revision(key);
        if (!_encodedSections.TryGetValue(key, out var payload) ||
            payload.Revision != revision)
        {
            byte[] encoded = EditSectionCodec.Encode(_edits.InSection(key));
            payload = (revision, encoded, EditSectionCodec.Hash(encoded));
            _encodedSections[key] = payload;
        }
        byte[] bytes = payload.Bytes;
        string hash = payload.Hash;
        if (page == 0 && clientRevision == revision && clientHash == hash)
        {
            Rpc(nameof(ReceiveSectionPage), clientId, key.X, key.Y, key.Z,
                revision, 0, -1, hash, "");
            return;
        }
        int pages = EditSectionCodec.Pages(bytes.Length);
        if (page >= pages)
            return;
        Rpc(nameof(ReceiveSectionPage), clientId, key.X, key.Y, key.Z,
            revision, page, pages, hash, EditSectionCodec.Page(bytes, page));
    }

    [Rpc(RpcTarget.Everyone)]
    private void ReceiveSectionPage(int clientId, int x, int y, int z,
        int revision, int page, int pages, string checksum, string payload)
    {
        if (Scene.IsServer || !Scene.IsNetworked ||
            Scene.LocalClientId != clientId || _appliedSnapshot != EditState)
            return;
        ChunkKey key = new(x, y, z);
        if (!_transfers.TryGetValue(key, out var transfer))
            return;
        int result = transfer.Receive(revision, page, pages,
            checksum, payload, Seed, out var entries);
        if (result != 2 || entries == null)
            return;

        List<BlockPosition> changed;
        try { changed = _edits.ReplaceSection(key, entries, revision, Seed); }
        catch (ArgumentException) { transfer.Reset(); return; }

        _clientReady.Add(key);
        _transfers.Remove(key);
        foreach (BlockPosition position in changed)
        {
            MarkBlockDirty(position);
            UpdateSurfaceOpening(position);
        }
        // Even a section with no edited blocks may now be generated.
        MarkDirty(key);
        try
        {
            if (transfer.CompletedBytes != null)
                WorldEditCache.Write(CacheFolder, _worldId, key,
                    revision, transfer.CompletedBytes);
        }
        catch (IOException exception)
        {
            Console.WriteLine($"Tallowcraft client cache unavailable: {exception.Message}");
        }
    }

    [Rpc(RpcTarget.Everyone)]
    private void ReceiveSectionDelta(int x, int y, int z,
        int revision, int localIndex, int state)
    {
        if (Scene.IsServer || !Scene.IsNetworked ||
            _appliedSnapshot != EditState || localIndex < 0 ||
            localIndex >= 4096 || state < 0 || state > ushort.MaxValue)
            return;
        ChunkKey key = new(x, y, z);
        if (!_clientReady.Contains(key))
            return; // A pending baseline includes this edit.
        BlockPosition position = EditSectionCodec.Position(key, localIndex);
        if (!_edits.ApplyDelta(key, revision, position, (ushort)state, Seed))
        {
            // Revision gap or malformed delta: do not mesh from stale state.
            _clientReady.Remove(key);
            _transfers.Remove(key);
            EnsureSectionReady(key);
            return;
        }
        MarkBlockDirty(position);
        UpdateSurfaceOpening(position);
    }

    private void StartNearestJobs(int cx, int cy, int cz)
    {
        int workers = Math.Clamp(WorkerCount, 1, Math.Max(1, Environment.ProcessorCount));
        if (_jobs.Count + _distantJobs.Count >= workers)
            return;

        if (_candidateListDirty)
        {
            int radius = Math.Clamp(RenderRadius, 1,
                QuadtreeLodEnabled
                    ? (int)MathF.Ceiling(TerrainQuadtree.DefaultFullRadius *
                        Math.Clamp(LodQuality, 0.65f, 1.6f) + TerrainQuadtree.Hysteresis + 1f)
                    : 20);
            int nearRadius = CollisionRadius();
            int vertical = Math.Clamp(VerticalRadius, 0, 2);
            _candidates.Clear();
            _candidateKeys.Clear();
            _nextCandidate = 0;

            void Consider(ChunkKey key, int priorityX, int priorityY,
                int priorityZ)
            {
                // No terrain, foliage, structure, or collision queries in
                // candidate discovery. This is just a cheap distance sort.
                if (_jobs.ContainsKey(key) ||
                    (_loaded.TryGetValue(key, out LiveChunk? live) && !live.Dirty) ||
                    (_emptyChunks.TryGetValue(key, out int emptyVersion) &&
                        emptyVersion == VersionFor(key)) ||
                    !_candidateKeys.Add(key))
                    return;
                _candidates.Add((key, ChunkScheduling.Priority(
                    key, priorityX, priorityY, priorityZ, nearRadius)));
            }

            for (int dy = -vertical; dy <= vertical; dy++)
            {
                int layer = cy + dy;
                if (layer < ChunkGenerator.MinChunkY ||
                    layer > ChunkGenerator.MaxChunkY)
                    continue;
                for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    ChunkKey key = new(cx + dx, layer, cz + dz);
                    if (ShouldStream(key, cx, cy, cz))
                        Consider(key, cx, cy, cz);
                }
            }

            // Dedicated servers and hosts additionally require colliders
            // near every remote player, regardless of this camera's view.
            if (Scene.IsServer)
                for (int i = 0; i < _playerCenters.Count; i++)
                {
                    ChunkKey center = _playerCenters[i];
                    float remoteHeight = _playerHeights[i];
                    for (int dy = -vertical; dy <= vertical; dy++)
                    for (int dz = -nearRadius; dz <= nearRadius; dz++)
                    for (int dx = -nearRadius; dx <= nearRadius; dx++)
                    {
                        int layer = center.Y + dy;
                        if (layer < ChunkGenerator.MinChunkY ||
                            layer > ChunkGenerator.MaxChunkY ||
                            (long)dx * dx + (long)dz * dz >
                                (long)nearRadius * nearRadius)
                            continue;
                        ChunkKey key = new(center.X + dx, layer, center.Z + dz);
                        if (!HiddenForPlayer(key, remoteHeight))
                            Consider(key, center.X, center.Y, center.Z);
                    }
                }

            _candidates.Sort((a, b) =>
            {
                int order = a.Distance.CompareTo(b.Distance);
                if (order != 0) return order;
                order = a.Key.Y.CompareTo(b.Key.Y);
                if (order != 0) return order;
                order = a.Key.X.CompareTo(b.Key.X);
                return order != 0 ? order : a.Key.Z.CompareTo(b.Key.Z);
            });
            _candidateListDirty = false;
        }

        var snapshot = _edits.Snapshot;
        int seed = Seed;
        int searchedThisFrame = 0;
        while (_jobs.Count + _distantJobs.Count < workers &&
            _nextCandidate < _candidates.Count)
        {
            // A newly joining client must not enqueue hundreds of requests or
            // hit the disk cache for every visible chunk in a single frame.
            if (!Scene.IsServer && searchedThisFrame++ >= 12)
                break;
            ChunkKey key = _candidates[_nextCandidate++].Key;
            if (_jobs.ContainsKey(key) ||
                (_emptyChunks.TryGetValue(key, out int emptyVersion) &&
                    emptyVersion == VersionFor(key)))
                continue;
            if (!Scene.IsServer && !EnsureSectionReady(key))
                continue;
            if (!_loaded.TryGetValue(key, out LiveChunk? live))
            {
                if (!ChunkGenerator.HasPotentialBlocks(key, seed, snapshot))
                    continue;
            }
            else if (!live.Dirty)
                continue;

            // A neighboring edited block can change exposed border faces.
            // Require the six face-neighbor baselines before generating.
            if (!Scene.IsServer)
            {
                bool neighborsReady = true;
                ChunkKey[] neighbors =
                {
                    new(key.X - 1, key.Y, key.Z),
                    new(key.X + 1, key.Y, key.Z),
                    new(key.X, key.Y - 1, key.Z),
                    new(key.X, key.Y + 1, key.Z),
                    new(key.X, key.Y, key.Z - 1),
                    new(key.X, key.Y, key.Z + 1)
                };
                foreach (ChunkKey near in neighbors)
                    if (near.Y >= ChunkGenerator.MinChunkY &&
                        near.Y <= ChunkGenerator.MaxChunkY &&
                        !EnsureSectionReady(near))
                        neighborsReady = false;
                if (!neighborsReady)
                    continue;
            }

            int version = VersionFor(key);
            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            CancellationToken token = cancellation.Token;
            long enqueuedAt = Stopwatch.GetTimestamp();
            Task<ChunkMesh> task = Task.Run(() =>
            {
                long started = Stopwatch.GetTimestamp();
                ChunkMesh mesh = ChunkGenerator.GenerateMesh(key, seed, snapshot, token);
                mesh.GenerationMilliseconds =
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return mesh;
            }, token);
            _jobs.Add(key, new ChunkJob(task, version, cancellation, enqueuedAt));
        }

        if (_nextCandidate >= _candidates.Count && _jobs.Count == 0)
            _streamExhausted = true;
    }

    private int DistantVersion(TerrainTileKey key) =>
        _distantVersions.TryGetValue(key, out int version) ? version : 0;

    private void RebuildDistantSelection(int cx, int cz)
    {
        if (QuadtreeLodEnabled)
            _distantSelection = TerrainQuadtree.Select(cx, cz, _lastForward,
                RenderRadius, SideRadius, BehindRadius, LodQuality,
                _distantSelection.Tiles);
        else
            _distantSelection = new TerrainSelection();

        foreach (var pair in _distantJobs)
            if (!_distantSelection.Tiles.Contains(pair.Key) ||
                pair.Value.Version != DistantVersion(pair.Key))
                pair.Value.Cancel.Cancel();

        _distantQueueDirty = true;
        _distantNeedsRetire = true;
    }

    private bool DistantTileReady(int x, int z)
    {
        for (int level = 1; level <= TerrainQuadtree.MaxLevel; level++)
        {
            TerrainTileKey tile = TerrainTileKey.At(x, z, level);
            if (_distantSelection.Tiles.Contains(tile))
                return _distantLoaded.TryGetValue(tile, out var current) &&
                    current.Version == DistantVersion(tile);
        }
        return false;
    }

    private bool SurfaceColumnReady(int x, int z, int cx, int cy, int cz)
    {
        var snapshot = _edits.Snapshot;
        for (int y = 0; y <= ChunkGenerator.MaxChunkY; y++)
        {
            ChunkKey section = new(x, y, z);
            if (!ShouldStream(section, cx, cy, cz))
                continue;
            if (!ChunkGenerator.HasPotentialBlocks(section, Seed, snapshot))
                continue;
            if (_loaded.ContainsKey(section) ||
                (_emptyChunks.TryGetValue(section, out int empty) &&
                    empty == VersionFor(section)))
                continue;
            return false;
        }
        return true;
    }

    private bool CanRetireDistant(TerrainTileKey old, int cx, int cy, int cz)
    {
        for (int z = old.Z; z < old.Z + old.Span; z++)
        for (int x = old.X; x < old.X + old.Span; x++)
        {
            if (_distantSelection.Full.Contains(new TerrainColumn(x, z)))
            {
                if (!SurfaceColumnReady(x, z, cx, cy, cz))
                    return false;
            }
            else if (_distantSelection.ContainsTile(x, z))
            {
                if (!DistantTileReady(x, z))
                    return false;
            }
            else if (!QuadtreeLodEnabled &&
                ShouldStream(new ChunkKey(x, 0, z), cx, cy, cz) &&
                !SurfaceColumnReady(x, z, cx, cy, cz))
                return false;
        }
        return true;
    }

    private void RetireDistant(int cx, int cy, int cz)
    {
        if (!_distantNeedsRetire) return;
        _distantNeedsRetire = false;
        _distantScratch.Clear();
        foreach (var pair in _distantLoaded)
        {
            if (_distantSelection.Tiles.Contains(pair.Key) ||
                !CanRetireDistant(pair.Key, cx, cy, cz))
                continue;
            if (pair.Value.Object.IsValid)
            {
                pair.Value.Renderer.ClearMesh();
                pair.Value.Object.Destroy();
            }
            _distantScratch.Add(pair.Key);
        }
        foreach (TerrainTileKey key in _distantScratch)
            _distantLoaded.Remove(key);
    }

    private void TickDistantTerrain(int cx, int cy, int cz, int uploadBudget)
    {
        _distantScratch.Clear();
        bool newTileUploaded = false;
        foreach (var pair in _distantJobs)
        {
            if (uploadBudget <= 0) break;
            if (!pair.Value.Task.IsCompleted) continue;
            uploadBudget--;
            TerrainTileKey key = pair.Key;
            var job = pair.Value;
            _distantScratch.Add(key);
            job.Cancel.Dispose();
            if (job.Task.IsFaulted)
            {
                Console.WriteLine($"Tallowcraft LOD tile {key} failed: {job.Task.Exception}");
                continue;
            }
            if (!job.Task.IsCompletedSuccessfully ||
                !QuadtreeLodEnabled ||
                !_distantSelection.Tiles.Contains(key) ||
                job.Version != DistantVersion(key))
            {
                _distantQueueDirty = true;
                continue;
            }

            ChunkMesh mesh = job.Task.Result;
            if (_distantLoaded.TryGetValue(key, out var current) &&
                current.Object.IsValid)
            {
                current.Renderer.SetMesh(mesh.Vertices, mesh.Triangles,
                    mesh.Normals, mesh.Uvs);
                _distantLoaded[key] = (current.Object, current.Renderer,
                    job.Version, mesh.Vertices.Length, mesh.Triangles.Length / 3);
            }
            else
            {
                GameObject tile = Scene.CreateGameObject(
                    $"Terrain LOD{key.Level} {key.X}, {key.Z}");
                tile.Transient = true;
                tile.Transform.Position = new Vector3(
                    key.X * ChunkGenerator.Size, 0f, key.Z * ChunkGenerator.Size);
                MeshRenderer renderer = tile.AddComponent<MeshRenderer>();
                renderer.FlatShading = true;
                renderer.BackFaceCulling = false; // skirts have two visible sides
                renderer.CastShadows = false;
                renderer.Texture = BlockTextureAtlas.Path;
                renderer.SetMesh(mesh.Vertices, mesh.Triangles,
                    mesh.Normals, mesh.Uvs);
                _distantLoaded[key] = (tile, renderer, job.Version,
                    mesh.Vertices.Length, mesh.Triangles.Length / 3);
            }

            _distantNeedsRetire = true;
            newTileUploaded = true;
            _generationMsTotal += mesh.GenerationMilliseconds;
            _generationSamples++;
            double milliseconds = Stopwatch.GetElapsedTime(job.Queued).TotalMilliseconds;
            _distantQueueLatency += milliseconds;
            _distantMaxLatency = Math.Max(_distantMaxLatency, milliseconds);
            if (++_distantUploads >= Math.Max(1, StreamReportEvery) && ProfileStreaming)
            {
                long vertices = 0, triangles = 0;
                foreach (var live in _distantLoaded.Values)
                {
                    vertices += live.Vertices;
                    triangles += live.Triangles;
                }
                long bytes = vertices * (12 + 12 + 8) + triangles * 12;
                Console.WriteLine(
                    $"[Tallowcraft LOD] tiles={_distantLoaded.Count} " +
                    $"full_sections={_loaded.Count} pending={_distantJobs.Count} " +
                    $"verts={vertices} tris={triangles} cpu_mesh_bytes={bytes} " +
                    $"queue_to_upload_avg_ms={_distantQueueLatency / _distantUploads:F1} " +
                    $"max_ms={_distantMaxLatency:F1}");
                _distantQueueLatency = _distantMaxLatency = 0;
                _distantUploads = 0;
            }
        }
        foreach (TerrainTileKey key in _distantScratch)
            _distantJobs.Remove(key);

        if (QuadtreeLodEnabled)
        {
            int workers = Math.Clamp(WorkerCount, 1,
                Math.Max(1, Environment.ProcessorCount));
            if (_distantQueueDirty)
            {
                _distantQueue.Clear();
                foreach (TerrainTileKey key in _distantSelection.Tiles)
                    if (!_distantJobs.ContainsKey(key) &&
                        (!_distantLoaded.TryGetValue(key, out var live) ||
                         live.Version != DistantVersion(key)))
                        _distantQueue.Add(key);
                _distantQueue.Sort((a, b) =>
                {
                    long ax = (long)a.X + a.Span / 2 - cx,
                        az = (long)a.Z + a.Span / 2 - cz,
                        bx = (long)b.X + b.Span / 2 - cx,
                        bz = (long)b.Z + b.Span / 2 - cz;
                    return (ax * ax + az * az).CompareTo(bx * bx + bz * bz);
                });
                _nextDistant = 0;
                _distantQueueDirty = false;
            }

            var edits = _edits.Snapshot;
            int seed = Seed;
            while (_jobs.Count + _distantJobs.Count < workers &&
                _nextDistant < _distantQueue.Count)
            {
                TerrainTileKey key = _distantQueue[_nextDistant++];
                if (_distantJobs.ContainsKey(key) ||
                    !_distantSelection.Tiles.Contains(key))
                    continue;
                if (_distantLoaded.TryGetValue(key, out var loaded) &&
                    loaded.Version == DistantVersion(key))
                    continue;
                int version = DistantVersion(key);
                var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                CancellationToken token = cancel.Token;
                long queued = Stopwatch.GetTimestamp();
                Task<ChunkMesh> task = Task.Run(() =>
                {
                    long started = Stopwatch.GetTimestamp();
                    ChunkMesh mesh = DistantTerrainMesher.Generate(key, seed, edits, token);
                    mesh.GenerationMilliseconds =
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    return mesh;
                }, token);
                _distantJobs.Add(key, (task, cancel, version, queued));
            }
        }
        RetireDistant(cx, cy, cz);
        // Remove obsolete full-detail chunks only after their replacement is
        // uploaded, without leaving a one-frame gap in the rendered world.
        if (newTileUploaded)
            UnloadDistant(cx, cy, cz);
    }

    private void ResetDistantTerrain()
    {
        foreach (var job in _distantJobs.Values)
        {
            job.Cancel.Cancel();
            job.Cancel.Dispose();
        }
        _distantJobs.Clear();
        foreach (var live in _distantLoaded.Values)
            if (live.Object.IsValid)
            {
                live.Renderer.ClearMesh();
                live.Object.Destroy();
            }
        _distantLoaded.Clear();
        _distantVersions.Clear();
        _distantSelection = new TerrainSelection();
        _distantQueue.Clear();
        _distantQueueDirty = _distantNeedsRetire = true;
        _nextDistant = 0;
    }

    /// <summary>
    /// The shared block targeting query for both preview and server edits.
    /// A client may raycast its local generated collision chunks, but it cannot
    /// change the authoritative edit state. Ignore the player's own capsule.
    /// </summary>
    public bool TryQueryFromRay(Vector3 origin, Vector3 direction, float reach,
        GameObject actingPlayer, out BlockPosition remove, out BlockPosition place) =>
        TryQueryFromRay(origin, direction, reach, actingPlayer,
            out remove, out place, out _);

    public bool TryQueryFromRay(Vector3 origin, Vector3 direction, float reach,
        GameObject actingPlayer, out BlockPosition remove, out BlockPosition place,
        out Vector3 hitNormal)
    {
        remove = default;
        place = default;
        hitNormal = default;
        if (!float.IsFinite(reach) || reach <= 0f ||
            !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) ||
            !float.IsFinite(origin.Z) || !float.IsFinite(direction.X) ||
            !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) ||
            direction.LengthSquared() < 0.0001f)
            return false;

        direction = Vector3.Normalize(direction);
        PhysicsHit? contact = null;
        foreach (PhysicsHit candidate in Scene.RaycastAll(
            origin, direction, Math.Clamp(reach, 1f, 8f)))
        {
            if (candidate.HitObject is { } target && target.Id == actingPlayer.Id)
                continue;
            contact = candidate;
            break;
        }

        if (contact is not { } hit || hit.Normal is not Vector3 normal ||
            hit.HitObject is not { } objectHit || !_chunkObjects.ContainsKey(objectHit.Id))
            return false;

        // Inside the struck half-cell, then advance a whole block in the
        // normal's direction. This is also the exact targeting rule used by
        // server placement, including stairs and lower/upper slabs.
        Vector3 inside = hit.Point - normal * 0.01f;
        remove = new BlockPosition((int)MathF.Floor(inside.X),
            (int)MathF.Floor(inside.Y), (int)MathF.Floor(inside.Z));
        place = new BlockPosition(remove.X + (int)MathF.Round(normal.X),
            remove.Y + (int)MathF.Round(normal.Y),
            remove.Z + (int)MathF.Round(normal.Z));
        hitNormal = normal;
        return ChunkGenerator.InWorld(remove.Y) &&
            _edits.Get(remove.X, remove.Y, remove.Z, Seed) != 0;
    }

    public ushort BlockAt(BlockPosition position) =>
        _edits.Get(position.X, position.Y, position.Z, Seed);

    /// <summary>
    /// Conservative spawn clearance, using authoritative edited terrain at
    /// the spawn X/Z position. Checks all columns touched by the controller
    /// radius and all occupied half-cells (including slabs and stairs).
    /// The returned Y is the center of a standing capsule, not its feet.
    /// </summary>
    public float SafeSpawnHeight(float x, float z, float standingHeight, float radius = 0.5f) =>
        ChunkGenerator.SpawnHeight(x, z, Seed, _edits.Snapshot, standingHeight, radius);

    /// <summary>Shared, side-effect-free placement rule for preview and server.</summary>
    public bool TryPlanPlacement(BlockPosition hit, BlockPosition adjacent,
        Vector3 hitNormal, BlockState selected,
        out BlockPosition target, out ushort replacement) =>
        BlockPlacement.TryPlan(hit, adjacent, hitNormal,
            new BlockState(BlockAt(hit)), selected, out target, out replacement);

    /// <summary>
    /// Validate a proposed state change against the voxel contents and nearby
    /// physics bodies. Check only each newly-filled half-cell, with a small
    /// gap to avoid false overlaps with terrain in neighboring cells.
    /// </summary>
    public bool CanPlaceAt(BlockPosition position, ushort replacement)
    {
        if (!ChunkGenerator.InWorld(position.Y))
            return false;

        ushort previous = BlockAt(position);
        if (!BlockPlacement.CanReplace(previous, replacement))
            return false;

        for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
                for (int x = 0; x < 2; x++)
                {
                    if (!BlockPlacement.AddsMicro(previous, replacement, x, y, z))
                        continue;

                    Vector3 center = new(position.X + (x == 0 ? 0.25f : 0.75f),
                        position.Y + (y == 0 ? 0.25f : 0.75f),
                        position.Z + (z == 0 ? 0.25f : 0.75f));
                    if (Scene.CheckBox(center, new Vector3(0.45f), Quaternion.Identity))
                        return false;
                }
        return true;
    }

    /// <summary>Server-owned chest inventory at a validated chest voxel.
    /// Clients never access mutable world chest data directly.</summary>
    public bool TryReadChest(BlockPosition position, out ChestContents contents)
    {
        contents = new ChestContents();
        if (!Scene.IsServer || _saveLoadFailed ||
            new BlockState(BlockAt(position)).Type != BlockInventory.Chest)
            return false;
        contents = _chests.At(position);
        return true;
    }

    public bool TryRearrangeChest(BlockPosition position, int from,
        int to, bool split)
    {
        if (!TryReadChest(position, out _) ||
            !_chests.TryRearrange(position, from, to, split))
            return false;
        _generation++;
        return true;
    }

    public BlockPosition? NearestChest(Vector3 position, float reach = 4.5f)
    {
        if (!Scene.IsServer || _saveLoadFailed || reach <= 0 ||
            !float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z)) return null;
        int minX = (int)MathF.Floor(position.X - reach);
        int minY = (int)MathF.Floor(position.Y - reach);
        int minZ = (int)MathF.Floor(position.Z - reach);
        int maxX = (int)MathF.Floor(position.X + reach);
        int maxY = (int)MathF.Floor(position.Y + reach);
        int maxZ = (int)MathF.Floor(position.Z + reach);
        float best = reach*reach;
        BlockPosition? closest = null;
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            var point = new BlockPosition(x,y,z);
            if (new BlockState(BlockAt(point)).Type != BlockInventory.Chest)
                continue;
            float dist = Vector3.DistanceSquared(position,
                new Vector3(x+.5f,y+.5f,z+.5f));
            if (dist > best) continue;
            best = dist;
            closest = point;
        }
        return closest;
    }

    /// <summary>Server-authoritative edit using the same target as preview.</summary>
    public bool TryEditFromRay(Vector3 origin, Vector3 direction, float reach,
        bool breaking, GameObject actingPlayer, ushort placeState) =>
        TryEditFromRay(origin, direction, reach, breaking, actingPlayer,
            placeState, out _, out _);

    /// <summary>
    /// Authoritative block transaction. Return the actual previous and next
    /// voxel states only after a successful edit, so a player's inventory can
    /// be credited or debited exactly once without a second raycast.
    /// </summary>
    public bool TryEditFromRay(Vector3 origin, Vector3 direction, float reach,
        bool breaking, GameObject actingPlayer, ushort placeState,
        out ushort previousState, out ushort replacementState) =>
        TryEditFromRay(origin, direction, reach, breaking, actingPlayer,
            placeState, out previousState, out replacementState,
            out _, out _, out _);

    /// <summary>Also exposes the exact modified voxel and immutable
    /// before-edit world snapshot so reward fallback can checkpoint the
    /// pre-mine state without undoing or rewinding live ECS revisions.</summary>
    public bool TryEditFromRay(Vector3 origin, Vector3 direction, float reach,
        bool breaking, GameObject actingPlayer, ushort placeState,
        out ushort previousState, out ushort replacementState,
        out BlockPosition editedPosition,
        out SectionedEditSnapshot beforeEdit,
        out IReadOnlyDictionary<ChunkKey, int> beforeRevisions)
    {
        previousState = 0;
        replacementState = 0;
        editedPosition = default;
        beforeEdit = _edits.Snapshot;
        beforeRevisions = _edits.Revisions;
        if (!Scene.IsServer || _saveLoadFailed ||
            (!breaking && (placeState == 0 || !BlockState.IsValid(placeState))) ||
            !TryQueryFromRay(origin, direction, reach, actingPlayer,
                out BlockPosition remove, out BlockPosition adjacent,
                out Vector3 hitNormal))
            return false;

        BlockPosition target;
        ushort replacement;
        if (breaking)
        {
            target = remove;
            replacement = 0;
        }
        else
        {
            if (!TryPlanPlacement(remove, adjacent, hitNormal,
                    new BlockState(placeState), out target, out replacement) ||
                !CanPlaceAt(target, replacement))
                return false;
        }

        ushort previous = BlockAt(target);
        if (breaking && previous == 0)
            return false;
        if (breaking && new BlockState(previous).Type == BlockInventory.Chest &&
            !_chests.IsEmpty(target))
            return false; // Empty a chest before mining it to avoid lost contents.
        if (!_edits.Set(target, replacement, Seed))
            return false;

        previousState = previous;
        replacementState = replacement;
        editedPosition = target;
        if (breaking && new BlockState(previous).Type == BlockInventory.Chest)
            _chests.Remove(target);
        MarkBlockDirty(target);
        UpdateSurfaceOpening(target);
        _generation++;
        ChunkKey section = SectionedEditSnapshot.SectionOf(target);
        _encodedSections.Remove(section);
        Rpc(nameof(ReceiveSectionDelta), section.X, section.Y, section.Z,
            _edits.Revision(section), (int)EditSectionCodec.Index(target),
            (int)replacement);
        return true;
    }

    public override void OnDestroy()
    {
        if (Scene.IsServer)
            SaveNow();
        _stop.Cancel();
        foreach (ChunkJob job in _jobs.Values)
            job.Cancellation.Cancel();
        ResetDistantTerrain();
        foreach (LiveChunk chunk in _loaded.Values)
        {
            if (!chunk.Object.IsValid)
                continue;
            chunk.Renderer.ClearMesh();
            chunk.Object.Destroy();
        }

        _loaded.Clear();
        _emptyChunks.Clear();
        _jobs.Clear();
        _colliders.Clear();
        _shadowCasters.Clear();
        _chunkObjects.Clear();
        _versions.Clear();
    }
}
