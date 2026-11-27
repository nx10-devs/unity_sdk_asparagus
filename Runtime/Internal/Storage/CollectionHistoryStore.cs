using System;
using System.IO;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Storage
{
    /// <summary>
    /// Fault-contained atomic persistence for environment-scoped on-device <see cref="CollectionHistory"/>
    /// (<c>asparagus_history.dat</c> for Production and <c>asparagus_history_stage.dat</c> for Staging).
    /// All <see cref="LoadFromDisk(AsparagusEnvironment)"/> and <see cref="SaveToDisk(AsparagusEnvironment, CollectionHistory)"/>
    /// calls MUST execute strictly off Unity's main thread on <c>AsparagusWorker</c>.
    /// </summary>
    public sealed class CollectionHistoryStore
    {
        public const string FileName = "asparagus_history.dat";
        public const string StagingFileName = "asparagus_history_stage.dat";
        private const string TempFileName = "asparagus_history.dat.tmp";
        private const string StagingTempFileName = "asparagus_history_stage.dat.tmp";

        private static readonly byte[] MagicBytes = { (byte)'A', (byte)'C', (byte)'H', (byte)'1' };
        private const byte FormatVersion = 1;
        private const int ExpectedFileBytes = 21; // 4 magic + 1 ver + 4 uint32 + 8 uint64 + 4 uint32

        private readonly string _baseDirectory;
        private readonly string _productionFilePath;
        private readonly string _productionTempPath;
        private readonly string _stagingFilePath;
        private readonly string _stagingTempPath;

        public CollectionHistoryStore(string baseDirectory)
        {
            _baseDirectory = string.IsNullOrEmpty(baseDirectory) ? Path.GetTempPath() : baseDirectory;
            _productionFilePath = Path.Combine(_baseDirectory, FileName);
            _productionTempPath = Path.Combine(_baseDirectory, TempFileName);
            _stagingFilePath = Path.Combine(_baseDirectory, StagingFileName);
            _stagingTempPath = Path.Combine(_baseDirectory, StagingTempFileName);
        }

        public string FilePath
        {
            get { return _productionFilePath; }
        }

        public string GetFilePath(AsparagusEnvironment environment)
        {
            return environment == AsparagusEnvironment.Staging
                ? _stagingFilePath
                : _productionFilePath;
        }

        public CollectionHistory LoadFromDisk()
        {
            return LoadFromDisk(AsparagusEnvironment.Production);
        }

        public CollectionHistory LoadFromDisk(AsparagusEnvironment environment)
        {
            string filePath = environment == AsparagusEnvironment.Staging ? _stagingFilePath : _productionFilePath;
            string tempPath = environment == AsparagusEnvironment.Staging ? _stagingTempPath : _productionTempPath;

            try
            {
                SafeDeleteFile(tempPath);

                if (!File.Exists(filePath))
                {
                    return default;
                }

                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length != ExpectedFileBytes)
                    {
                        return default;
                    }

                    using (BinaryReader br = new BinaryReader(fs, System.Text.Encoding.UTF8, false))
                    {
                        byte[] magic = br.ReadBytes(4);
                        if (magic.Length != 4
                            || magic[0] != MagicBytes[0]
                            || magic[1] != MagicBytes[1]
                            || magic[2] != MagicBytes[2]
                            || magic[3] != MagicBytes[3])
                        {
                            return default;
                        }

                        byte version = br.ReadByte();
                        if (version != FormatVersion)
                        {
                            return default;
                        }

                        uint sessionsBefore = br.ReadUInt32();
                        ulong collectedSecondsBefore = br.ReadUInt64();
                        uint longestSessionSecondsBefore = br.ReadUInt32();
                        return new CollectionHistory(
                            sessionsBefore,
                            collectedSecondsBefore,
                            longestSessionSecondsBefore);
                    }
                }
            }
            catch
            {
                return default;
            }
        }

        public bool SaveToDisk(CollectionHistory snapshot)
        {
            return SaveToDisk(AsparagusEnvironment.Production, snapshot);
        }

        public bool SaveToDisk(AsparagusEnvironment environment, CollectionHistory snapshot)
        {
            string filePath = environment == AsparagusEnvironment.Staging ? _stagingFilePath : _productionFilePath;
            string tempPath = environment == AsparagusEnvironment.Staging ? _stagingTempPath : _productionTempPath;

            try
            {
                if (!Directory.Exists(_baseDirectory))
                {
                    Directory.CreateDirectory(_baseDirectory);
                }

                using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64))
                using (BinaryWriter bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, false))
                {
                    bw.Write(MagicBytes);
                    bw.Write(FormatVersion);
                    bw.Write(snapshot.SessionsBefore);
                    bw.Write(snapshot.CollectedSecondsBefore);
                    bw.Write(snapshot.LongestSessionSecondsBefore);
                    bw.Flush();
                }

                if (File.Exists(filePath))
                {
                    SafeDeleteFile(filePath);
                }

                File.Move(tempPath, filePath);
                return true;
            }
            catch
            {
                SafeDeleteFile(tempPath);
                return false;
            }
        }

        private static void SafeDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
