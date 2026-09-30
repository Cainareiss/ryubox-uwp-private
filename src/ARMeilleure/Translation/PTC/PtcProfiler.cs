using ARMeilleure.State;
using Humanizer;
using Microsoft.IO;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using static ARMeilleure.Translation.PTC.PtcFormatter;
using Timer = System.Timers.Timer;

namespace ARMeilleure.Translation.PTC
{
    class PtcProfiler
    {
        private const string OuterHeaderMagicString = "Pohd\0\0\0\0";

        private const uint InternalVersion = 7031; //! Not to be incremented manually for each change to the ARMeilleure project.

        private static readonly uint[] _migrateInternalVersions =
        [
            1866,
            5518,
        ];

        private const int SaveInterval = 30; // Seconds.

        private const CompressionLevel SaveCompressionLevel = CompressionLevel.Fastest;

        private readonly Ptc _ptc;

        private readonly Timer _timer;

        private readonly ulong _outerHeaderMagic;

        private readonly ManualResetEvent _waitEvent;

        private readonly Lock _lock = new();
        private readonly Lock _lifecycleLock = new();

        private bool _disposed;
        private bool _writerRunning;
        private bool _actualFileValid;

        private Hash128 _lastHash;

        public Dictionary<ulong, FuncProfile> ProfiledFuncs { get; private set; }

        public bool Enabled { get; private set; }

        public ulong StaticCodeStart { get; set; }
        public ulong StaticCodeSize { get; set; }

        public PtcProfiler(Ptc ptc)
        {
            _ptc = ptc;

            _timer = new Timer(SaveInterval.Seconds());
            _timer.Elapsed += TimerElapsed;

            _outerHeaderMagic = BinaryPrimitives.ReadUInt64LittleEndian(EncodingCache.UTF8NoBOM.GetBytes(OuterHeaderMagicString).AsSpan());

            _waitEvent = new ManualResetEvent(true);

            _disposed = false;

            ProfiledFuncs = new Dictionary<ulong, FuncProfile>();

            Enabled = false;
        }

        private void TimerElapsed(object _, ElapsedEventArgs __)
        {
            lock (_lock)
            {
                if (_disposed || !Enabled || _writerRunning) return;
                _writerRunning = true;
                _waitEvent.Reset();
                try { new Thread(PreSave) { Name = "Ptc.DiskWriter", IsBackground = true }.Start(); }
                catch { _writerRunning = false; _waitEvent.Set(); throw; }
            }
        }

        public void AddEntry(ulong address, ExecutionMode mode, bool highCq, bool blacklist = false)
        {
            if (IsAddressInStaticCodeRange(address))
            {
                Debug.Assert(!highCq);

                if (blacklist)
                {
                    lock (_lock)
                    {
                        ProfiledFuncs[address] = new FuncProfile(mode, highCq: false, true);
                    }
                }
                else
                {
                    lock (_lock)
                    {
                        ProfiledFuncs.TryAdd(address, new FuncProfile(mode, highCq: false, false));
                    }
                }
            }
        }

        public void UpdateEntry(ulong address, ExecutionMode mode, bool highCq, bool? blacklist = null)
        {
            if (IsAddressInStaticCodeRange(address))
            {
                Debug.Assert(highCq);

                lock (_lock)
                {
                    // Background translation can promote a static function
                    // before the profiling path has recorded its low-CQ entry
                    // (notably after an interrupted or empty .info file).
                    // Treat that as a new profile entry instead of letting a
                    // dictionary indexer terminate the emulation thread.
                    if (!ProfiledFuncs.TryGetValue(address, out FuncProfile current))
                    {
                        ProfiledFuncs[address] = new FuncProfile(mode, highCq: true, blacklist ?? false);
                        return;
                    }

                    ProfiledFuncs[address] = new FuncProfile(mode, highCq: true, blacklist ?? current.Blacklist);
                }
            }
        }

        public bool IsAddressInStaticCodeRange(ulong address)
        {
            return address >= StaticCodeStart && address < StaticCodeStart + StaticCodeSize;
        }

        public ConcurrentQueue<(ulong address, FuncProfile funcProfile)> GetProfiledFuncsToTranslate(TranslatorCache<TranslatedFunction> funcs)
        {
            ConcurrentQueue<(ulong address, FuncProfile funcProfile)> profiledFuncsToTranslate = new();

            foreach (KeyValuePair<ulong, FuncProfile> profiledFunc in ProfiledFuncs)
            {
                if (!funcs.ContainsKey(profiledFunc.Key) && !profiledFunc.Value.Blacklist)
                {
                    profiledFuncsToTranslate.Enqueue((profiledFunc.Key, profiledFunc.Value));
                }
            }

            return profiledFuncsToTranslate;
        }

        public void ClearEntries()
        {
            ProfiledFuncs.Clear();
            ProfiledFuncs.TrimExcess();
        }

        public List<ulong> GetBlacklistedFunctions()
        {
            List<ulong> funcs = [];

            foreach ((ulong ptr, FuncProfile funcProfile) in ProfiledFuncs)
            {
                if (!funcProfile.Blacklist)
                    continue;

                if (!funcs.Contains(ptr))
                    funcs.Add(ptr);
            }

            return funcs;
        }

        public void PreLoad()
        {
            _lastHash = default;
            _actualFileValid = false;

            string fileNameActual = $"{_ptc.CachePathActual}.info";
            string fileNameBackup = $"{_ptc.CachePathBackup}.info";

            FileInfo fileInfoActual = new(fileNameActual);
            FileInfo fileInfoBackup = new(fileNameBackup);

            if (fileInfoActual.Exists && fileInfoActual.Length != 0L)
            {
                if (!Load(fileNameActual, false))
                {
                    if (fileInfoBackup.Exists && fileInfoBackup.Length != 0L)
                    {
                        Load(fileNameBackup, true);
                    }
                }
            }
            else if (fileInfoBackup.Exists && fileInfoBackup.Length != 0L)
            {
                Load(fileNameBackup, true);
            }
        }

        private bool Load(string fileName, bool isBackup)
        {
            try
            {
                bool loaded = LoadCore(fileName, isBackup);
                if (!isBackup) _actualFileValid = loaded;
                return loaded;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException or OverflowException or OutOfMemoryException)
            {
                if (!isBackup) _actualFileValid = false;
                ClearEntries();
                Logger.Warning?.Print(LogClass.Ptc,
                    $"PTC profile rejected: path='{fileName}', backup={isBackup}, reason={exception.GetType().Name}: {exception.Message}; trying backup or cold profiling.");
                return false;
            }
        }

        private bool LoadCore(string fileName, bool isBackup)
        {
            using (FileStream compressedStream = new(fileName, FileMode.Open))
            using (DeflateStream deflateStream = new(compressedStream, CompressionMode.Decompress, true))
            {
                OuterHeader outerHeader = DeserializeStructure<OuterHeader>(compressedStream);

                if (!outerHeader.IsHeaderValid())
                {
                    InvalidateCompressedStream(compressedStream);

                    return false;
                }

                if (outerHeader.Magic != _outerHeaderMagic)
                {
                    InvalidateCompressedStream(compressedStream);

                    return false;
                }

                if (outerHeader.InfoFileVersion != InternalVersion && !_migrateInternalVersions.Contains(outerHeader.InfoFileVersion))
                {
                    InvalidateCompressedStream(compressedStream);

                    return false;
                }

                if (outerHeader.Endianness != Ptc.GetEndianness())
                {
                    InvalidateCompressedStream(compressedStream);

                    return false;
                }

                using RecyclableMemoryStream stream = MemoryStreamManager.Shared.GetStream();
                Debug.Assert(stream.Seek(0L, SeekOrigin.Begin) == 0L && stream.Length == 0L);

                // Profiling metadata is not machine code. Even millions of
                // entries fit in this bound; reject a decompression bomb before
                // it exhausts the sandbox's managed heap.
                const int maxProfileBytes = 32 * 1024 * 1024;
                byte[] copyBuffer = new byte[8192];
                int read;
                while ((read = deflateStream.Read(copyBuffer)) != 0)
                {
                    if (stream.Length + read > maxProfileBytes)
                        throw new InvalidDataException("PTC profile exceeds the metadata budget.");
                    stream.Write(copyBuffer, 0, read);
                }

                Debug.Assert(stream.Position == stream.Length);

                stream.Seek(0L, SeekOrigin.Begin);

                Hash128 expectedHash = DeserializeStructure<Hash128>(stream);

                Hash128 actualHash = Hash128.ComputeHash(GetReadOnlySpan(stream));

                if (actualHash != expectedHash)
                {
                    InvalidateCompressedStream(compressedStream);

                    return false;
                }

                Func<ulong, FuncProfile, (ulong, FuncProfile)> migrateEntryFunc = null;

                switch (outerHeader.InfoFileVersion)
                {
                    case InternalVersion:
                        ProfiledFuncs = Deserialize(stream);
                        break;
                    case 1866:
                        migrateEntryFunc = (address, profile) => (address + 0x500000UL, profile);
                        goto case 5518;
                    case 5518:
                        ProfiledFuncs = DeserializeAddBlacklist(stream, migrateEntryFunc);
                        break;
                    default:
                        Logger.Error?.Print(LogClass.Ptc, $"No migration path for {nameof(outerHeader.InfoFileVersion)} '{outerHeader.InfoFileVersion}'. Discarding cache.");
                        InvalidateCompressedStream(compressedStream);
                        return false;
                }

                Debug.Assert(stream.Position == stream.Length);

                _lastHash = actualHash;
            }

            long fileSize = new FileInfo(fileName).Length;

            Logger.Info?.Print(
                LogClass.Ptc,
                $"{(isBackup ? "Loaded Backup Profiling Info" : "Loaded Profiling Info")} " +
                $"(pid: {_ptc.CacheInfo.ProcessId}, title: {_ptc.CacheInfo.TitleIdText}, version: '{_ptc.CacheInfo.DisplayVersion}', " +
                $"kind: {_ptc.CacheInfo.ProcessKind}, selector: '{_ptc.CacheInfo.CacheSelector}', key: '{_ptc.CacheInfo.CacheKey}', " +
                $"path: '{fileName}', size: {fileSize} bytes, profiled functions: {ProfiledFuncs.Count}).");

            return true;
        }

        private static Dictionary<ulong, FuncProfile> Deserialize(Stream stream, Func<ulong, FuncProfile, (ulong, FuncProfile)> migrateEntryFunc = null)
        {
            if (migrateEntryFunc != null)
            {
                return DeserializeAndUpdateDictionary(stream, DeserializeStructure<FuncProfile>, migrateEntryFunc);
            }

            return DeserializeDictionary<ulong, FuncProfile>(stream, DeserializeStructure<FuncProfile>);
        }

        private static Dictionary<ulong, FuncProfile> DeserializeAddBlacklist(Stream stream, Func<ulong, FuncProfile, (ulong, FuncProfile)> migrateEntryFunc = null)
        {
            if (migrateEntryFunc != null)
            {
                return DeserializeAndUpdateDictionary(stream, stream => { return new FuncProfile(DeserializeStructure<FuncProfilePreBlacklist>(stream)); }, migrateEntryFunc);
            }

            return DeserializeDictionary<ulong, FuncProfile>(stream, stream => { return new FuncProfile(DeserializeStructure<FuncProfilePreBlacklist>(stream)); });
        }

        private static ReadOnlySpan<byte> GetReadOnlySpan(MemoryStream memoryStream)
        {
            return new(memoryStream.GetBuffer(), (int)memoryStream.Position, (int)memoryStream.Length - (int)memoryStream.Position);
        }

        private static void InvalidateCompressedStream(FileStream compressedStream)
        {
            compressedStream.SetLength(0L);
        }

        private void PreSave()
        {
            try
            {
                string fileNameActual = $"{_ptc.CachePathActual}.info";
                string fileNameBackup = $"{_ptc.CachePathBackup}.info";
                FileInfo fileInfoActual = new(fileNameActual);
                if (_actualFileValid && fileInfoActual.Exists && fileInfoActual.Length != 0L)
                {
                    File.Copy(fileNameActual, fileNameBackup, true);
                }
                Save(fileNameActual);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                Logger.Warning?.Print(LogClass.Ptc,
                    $"PTC profile save failed: path='{_ptc.CachePathActual}', reason={exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _writerRunning = false;
                    _waitEvent.Set();
                }
            }
        }

        private void Save(string fileName)
        {
            int profiledFuncsCount;

            OuterHeader outerHeader = new()
            {
                Magic = _outerHeaderMagic,

                InfoFileVersion = InternalVersion,
                Endianness = Ptc.GetEndianness(),
            };

            outerHeader.SetHeaderHash();

            using (MemoryStream stream = MemoryStreamManager.Shared.GetStream())
            {
                Debug.Assert(stream.Seek(0L, SeekOrigin.Begin) == 0L && stream.Length == 0L);

                stream.Seek(Unsafe.SizeOf<Hash128>(), SeekOrigin.Begin);

                lock (_lock)
                {
                    Serialize(stream, ProfiledFuncs);

                    profiledFuncsCount = ProfiledFuncs.Count;
                }

                Debug.Assert(stream.Position == stream.Length);

                stream.Seek(Unsafe.SizeOf<Hash128>(), SeekOrigin.Begin);
                Hash128 hash = Hash128.ComputeHash(GetReadOnlySpan(stream));

                stream.Seek(0L, SeekOrigin.Begin);
                SerializeStructure(stream, hash);

                if (hash == _lastHash && _actualFileValid)
                {
                    return;
                }

                PtcCacheFile.Write(fileName, compressedStream =>
                {
                    SerializeStructure(compressedStream, outerHeader);
                    using DeflateStream deflateStream = new(compressedStream, SaveCompressionLevel, true);
                    stream.WriteTo(deflateStream);
                });
                _lastHash = hash;
                _actualFileValid = true;
            }

            long fileSize = new FileInfo(fileName).Length;

            if (fileSize != 0L)
            {
                Logger.Info?.Print(
                    LogClass.Ptc,
                    $"Saved Profiling Info (pid: {_ptc.CacheInfo.ProcessId}, title: {_ptc.CacheInfo.TitleIdText}, version: '{_ptc.CacheInfo.DisplayVersion}', " +
                    $"kind: {_ptc.CacheInfo.ProcessKind}, selector: '{_ptc.CacheInfo.CacheSelector}', key: '{_ptc.CacheInfo.CacheKey}', " +
                    $"path: '{fileName}', size: {fileSize} bytes, profiled functions: {profiledFuncsCount}).");
            }
        }

        private static void Serialize(Stream stream, Dictionary<ulong, FuncProfile> profiledFuncs)
        {
            SerializeDictionary(stream, profiledFuncs, SerializeStructure);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1/*, Size = 29*/)]
        private struct OuterHeader
        {
            public ulong Magic;

            public uint InfoFileVersion;

            public bool Endianness;

            public Hash128 HeaderHash;

            public void SetHeaderHash()
            {
                Span<OuterHeader> spanHeader = MemoryMarshal.CreateSpan(ref this, 1);

                HeaderHash = Hash128.ComputeHash(MemoryMarshal.AsBytes(spanHeader)[..(Unsafe.SizeOf<OuterHeader>() - Unsafe.SizeOf<Hash128>())]);
            }

            public bool IsHeaderValid()
            {
                Span<OuterHeader> spanHeader = MemoryMarshal.CreateSpan(ref this, 1);

                return Hash128.ComputeHash(MemoryMarshal.AsBytes(spanHeader)[..(Unsafe.SizeOf<OuterHeader>() - Unsafe.SizeOf<Hash128>())]) == HeaderHash;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1/*, Size = 6*/)]
        public struct FuncProfile
        {
            public ExecutionMode Mode;
            public bool HighCq;
            public bool Blacklist;

            public FuncProfile(ExecutionMode mode, bool highCq, bool blacklist)
            {
                Mode = mode;
                HighCq = highCq;
                Blacklist = blacklist;
            }

            public FuncProfile(FuncProfilePreBlacklist fp)
            {
                Mode = fp.Mode;
                HighCq = fp.HighCq;
                Blacklist = false;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1/*, Size = 5*/)]
        public struct FuncProfilePreBlacklist
        {
            public ExecutionMode Mode;
            public bool HighCq;

            public FuncProfilePreBlacklist(ExecutionMode mode, bool highCq)
            {
                Mode = mode;
                HighCq = highCq;
            }
        }

        public void Start()
        {
            lock (_lifecycleLock)
            lock (_lock)
            {
                if (_disposed) return;
                if (_ptc.State is PtcState.Enabled or PtcState.Continuing)
                {
                    Enabled = true;
                    _timer.Enabled = true;
                }
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                bool flush;
                lock (_lock)
                {
                    if (_disposed) return;
                    flush = Enabled;
                    Enabled = false;
                    _timer.Enabled = false;
                }
                Wait();
                // Short sessions still need a profile for the next boot.
                if (flush && !string.IsNullOrEmpty(_ptc.CachePathActual))
                {
                    lock (_lock) { _writerRunning = true; _waitEvent.Reset(); }
                    PreSave();
                }
            }
        }

        public void Wait()
        {
            _waitEvent.WaitOne();
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                Stop();
                lock (_lock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _timer.Elapsed -= TimerElapsed;
                    _timer.Dispose();
                }
                Wait();
                _waitEvent.Dispose();
            }
        }
    }
}
