using System;
using System.Collections.Generic;
using System.IO;

namespace WorldGeneration;

/// <summary>
/// One in-flight section baseline. Pages must describe one stable revision,
/// arrive in order and match a SHA-256 digest before anything becomes visible.
/// A cached section is used only after the server confirms revision + digest.
/// </summary>
public sealed class EditSectionTransfer
{
    public readonly ChunkKey Key;
    public int NextPage { get; private set; }
    public float RetrySeconds;
    public int CacheRevision = -1;
    public string CacheHash = "";
    public Dictionary<BlockPosition, ushort>? Cached;
    public byte[]? CompletedBytes { get; private set; }

    private int _revision = -1;
    private int _pageCount;
    private string _hash = "";
    private readonly MemoryStream _buffer = new();

    public EditSectionTransfer(ChunkKey key) => Key = key;

    public void Reset()
    {
        NextPage = 0;
        _revision = -1;
        _pageCount = 0;
        _hash = "";
        _buffer.SetLength(0);
        CacheRevision = -1;
        CacheHash = "";
        Cached = null;
        CompletedBytes = null;
        RetrySeconds = 0f;
    }

    /// <returns>0: bad/retry from beginning; 1: next page; 2: complete.</returns>
    public int Receive(int revision, int page, int pages, string checksum,
        string encoded, int seed, out Dictionary<BlockPosition, ushort>? result)
    {
        result = null;
        if (revision < 0 || page < 0 || pages < -1 || pages > 64 ||
            checksum.Length != 64)
        {
            Reset();
            return 0;
        }

        if (page == 0 && pages == -1)
        {
            if (encoded.Length == 0 && revision == CacheRevision &&
                checksum == CacheHash && Cached != null)
            {
                result = Cached;
                CompletedBytes = EditSectionCodec.Encode(Cached);
                return 2;
            }
            Reset();
            return 0;
        }

        if (pages < 1 || page >= pages ||
            !EditSectionCodec.TryPage(encoded, out byte[] bytes))
        {
            Reset();
            return 0;
        }

        if (page == 0)
        {
            if (NextPage != 0)
            {
                Reset();
                return 0;
            }
            _revision = revision;
            _pageCount = pages;
            _hash = checksum;
            _buffer.SetLength(0);
        }
        if (page != NextPage || _revision != revision ||
            _pageCount != pages || _hash != checksum ||
            (page < pages - 1 && bytes.Length != EditSectionCodec.PageEdits * 4))
        {
            Reset();
            return 0;
        }

        _buffer.Write(bytes);
        NextPage++;
        RetrySeconds = 0f;
        if (NextPage < pages)
            return 1;

        byte[] complete = _buffer.ToArray();
        if (EditSectionCodec.Pages(complete.Length) != pages ||
            EditSectionCodec.Hash(complete) != checksum ||
            !EditSectionCodec.TryDecode(Key, seed, complete, out var decoded))
        {
            Reset();
            return 0;
        }

        result = decoded;
        CompletedBytes = complete;
        return 2;
    }

    public int Revision => _revision < 0 ? CacheRevision : _revision;
}
