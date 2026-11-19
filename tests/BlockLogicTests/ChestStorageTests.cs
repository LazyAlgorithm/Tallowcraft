using System;
using System.IO;
using System.Linq;
using System.Numerics;
using WorldGeneration;

internal static class ChestStorageTests
{
    public static void Run(Action<bool,string> check)
    {
        BlockPosition position=new(-33,21,-4);
        var player=new BlockInventory();
        player.FillStarter();
        var storage=new WorldChestStorage();
        check(storage.Count==0 && storage.IsEmpty(position) &&
            player.Slot(5)==new ItemStack(6,64),
            "New chests have empty inventories and no saved world record");

        check(storage.TryTransfer(position,player,5,0,true,true) &&
            storage.At(position).Slot(0)==new ItemStack(6,32) &&
            player.Slot(5)==new ItemStack(6,32),
            "Depositing half a stack moves exactly half into chest storage");
        var snapshot=storage.Snapshot;
        check(storage.TryTransfer(position,player,5,0,true,false) &&
            storage.At(position).Slot(0)==new ItemStack(6,64) &&
            player.Slot(5).IsEmpty &&
            snapshot[position].Slot(0)==new ItemStack(6,32),
            "Deposits merge to 64 and do not mutate earlier save snapshots");
        check(!storage.IsEmpty(position) &&
            !storage.TryTransfer(position,player,0,0,true,false),
            "Empty player slot cannot create items in a chest");
        check(!storage.TryTransfer(position,player,0,0,false,false) &&
            player.Slot(0)==new ItemStack(1,32),
            "Chest withdrawals reject incompatible occupied player slots");
        check(storage.TryTransfer(position,player,9,0,false,true) &&
            storage.At(position).Slot(0)==new ItemStack(6,32) &&
            player.Slot(9)==new ItemStack(6,32),
            "Withdrawing half stores only the exact amount in an empty backpack slot");
        check(storage.TryRearrange(position,0,1,false) &&
            storage.At(position).Slot(0).IsEmpty &&
            storage.At(position).Slot(1)==new ItemStack(6,32),
            "Players can rearrange full chest stacks without losing items");
        check(storage.TryTransfer(position,player,10,1,false,false) &&
            storage.IsEmpty(position) && storage.Count==0 &&
            player.Slot(10)==new ItemStack(6,32),
            "Emptying a chest removes its sparse world record");
        check(!storage.TryTransfer(position,player,5,99,true,false) &&
            !storage.TryTransfer(position,player,-1,0,true,false) &&
            !storage.TryRearrange(position,0,99,false),
            "Invalid chest slots cannot mutate world or player inventory");

        check(player.TryCollect(BlockInventory.Chest) &&
            player.Count(BlockInventory.Chest)==1 &&
            new BlockState(BlockState.Create(BlockInventory.Chest).Packed)
                .Type==BlockInventory.Chest &&
            BlockState.Create(BlockInventory.IronIngot).Packed==0 &&
            !BlockState.IsValid(BlockInventory.IronIngot),
            "Storage chest is a placeable voxel and iron ingots stay items");
        check(BlockState.Create(BlockInventory.Chest,BlockShape.Slab).Packed==0 &&
            BlockState.Create(BlockInventory.Chest,BlockShape.Stair).Packed==0,
            "Chests cannot become half-width or stair-shaped storage");

        check(storage.TryTransfer(position,player,0,2,true,false) &&
            storage.At(position).Slot(2)==new ItemStack(1,32),
            "Moving blocks to a chest makes a nonempty storage record");
        string serialized=storage.At(position).Serialize();
        check(ChestContents.TryLoad(serialized,out ChestContents restored) &&
            restored.Serialize()==serialized &&
            !ChestContents.TryLoad(serialized.Replace("1:32","1:999"),
                out _),
            "Chest UI snapshots roundtrip and reject forged oversized stacks");

        byte[] packed=ChestSaveCodec.Encode(storage.Snapshot);
        check(ChestSaveCodec.TryDecode(packed,out var decoded) &&
            decoded[position].Serialize()==serialized &&
            !ChestSaveCodec.TryDecode(packed[..^1],out _),
            "World chest snapshots roundtrip and reject truncation");
        byte[] invalid=(byte[])packed.Clone();
        invalid[8+12]=BlockInventory.IronIngot;
        invalid[8+13]=255;
        check(!ChestSaveCodec.TryDecode(invalid,out _),
            "Malformed chest stack values are rejected before world restoration");

        var faces=new[] {Vector3.UnitZ,Vector3.UnitX,-Vector3.UnitZ,-Vector3.UnitX};
        for(int facing=0;facing<4;facing++)
        {
            ChestFrontMesh.Build(facing,out var vertices,out var normals,
                out var uvs,out var triangles,out var offset);
            check(vertices.Length==4 && normals.Length==4 &&
                uvs.Length==4 && triangles.Length==6 &&
                Vector3.Dot(offset,faces[facing])>.5f &&
                normals.All(n=>n==faces[facing]),
                "Chest lid overlay rotates to the voxel's four facings");
        }

        string root=Path.Combine(Path.GetTempPath(),
            "tallowcraft-chest-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            string id=WorldEditSave.NewWorldId();
            const int seed=84;
            var edits=new BlockEdits();
            check(edits.Set(position,BlockState.Create(
                    BlockInventory.Chest,BlockShape.Cube,1).Packed,seed),
                "Placed chest is represented in sparse world edits");
            WorldEditSave.Save(root,id,seed,edits.Snapshot,
                edits.Revisions,storage.Snapshot);
            check(WorldEditSave.TryLoad(root,out string worldId,
                    out int restoredSeed,out var placed,
                    out WorldChestStorage content,out bool recovered) &&
                !recovered && worldId==id && restoredSeed==seed &&
                new BlockState(placed.Get(position.X,position.Y,
                    position.Z,seed)).Type==BlockInventory.Chest &&
                content.At(position).Serialize()==serialized,
                "Chests and their items restore from the same committed world generation");

            check(content.TryTransfer(position,player,9,3,true,false),
                "Chest contents can change after the first world save");
            WorldEditSave.Save(root,id,seed,edits.Snapshot,
                edits.Revisions,content.Snapshot);

            string generation=File.ReadAllText(
                Path.Combine(root,"current.txt")).Trim();
            File.WriteAllBytes(Path.Combine(root,"generations",
                generation,"chests.bin"),new byte[] {9,8,7});
            check(WorldEditSave.TryLoad(root,out _,out _,out _,
                    out WorldChestStorage backup,out bool fallback) &&
                fallback && backup.At(position).Serialize()==serialized,
                "Damaged chest data rolls the whole world back to the previous generation");

            // An orphan chest must not appear if the block is missing.
            var emptyEdits=new BlockEdits();
            WorldEditSave.Save(root,id,seed,emptyEdits.Snapshot,
                emptyEdits.Revisions,storage.Snapshot);
            check(WorldEditSave.TryLoad(root,out _,out _,out _,
                    out WorldChestStorage recoveredChests,out bool mismatch) &&
                mismatch && recoveredChests.Count>0,
                "Orphaned storage records cannot load without matching chest blocks");
        }
        finally
        {
            if(Directory.Exists(root)) Directory.Delete(root,recursive:true);
        }
        Console.WriteLine("PASS: 27-slot chests, atomic transfers, " +
            "oriented art and coupled world save recovery");
    }
}
