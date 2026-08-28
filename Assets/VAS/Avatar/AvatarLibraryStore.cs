using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

public sealed class AvatarLibraryStore
{
    [Serializable]
    public sealed class AvatarEntry
    {
        public string id;
        public string displayName;
        public string registeredUtc;
        public string lastUsedUtc;

        public string Id => id;
        public string DisplayName => displayName;
    }

    [Serializable]
    private sealed class LibraryData
    {
        public int schemaVersion = CurrentSchemaVersion;
        public string activeAvatarId = string.Empty;
        public bool legacyArmSettingsMigrated;
        public List<AvatarEntry> avatars = new();
    }

    public sealed class PendingImport : IDisposable
    {
        private readonly AvatarLibraryStore _owner;
        private bool _finished;

        internal PendingImport(
            AvatarLibraryStore owner,
            string id,
            string displayName,
            string temporaryPath)
        {
            _owner = owner;
            Id = id;
            DisplayName = displayName;
            TemporaryPath = temporaryPath;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string TemporaryPath { get; }
        internal AvatarLibraryStore Owner => _owner;
        internal bool IsFinished => _finished;

        internal void MarkFinished()
        {
            _finished = true;
        }

        public void Dispose()
        {
            if (_finished) return;
            _finished = true;
            TryDeleteFile(TemporaryPath);
        }
    }

    private const int CurrentSchemaVersion = 1;
    private const string RegistryFileName = "avatar-library.json";
    private const string RegistryBackupFileName = "avatar-library.json.bak";
    private const string CacheFolderName = "Cache";
    private const string TemporaryFolderName = "Temp";
    private const string TrashFolderName = "Trash";

    private readonly string _rootDirectory;
    private readonly string _cacheDirectory;
    private readonly string _temporaryDirectory;
    private readonly string _trashDirectory;
    private readonly string _registryPath;
    private readonly string _registryBackupPath;
    private readonly List<string> _prunedAvatarIds = new();
    private LibraryData _data = new();

    public AvatarLibraryStore(string persistentDataPath)
    {
        if (string.IsNullOrWhiteSpace(persistentDataPath))
            throw new ArgumentException("영구 데이터 경로가 비어 있습니다.", nameof(persistentDataPath));

        _rootDirectory = Path.GetFullPath(Path.Combine(persistentDataPath, "AvatarLibrary"));
        _cacheDirectory = Path.Combine(_rootDirectory, CacheFolderName);
        _temporaryDirectory = Path.Combine(_rootDirectory, TemporaryFolderName);
        _trashDirectory = Path.Combine(_rootDirectory, TrashFolderName);
        _registryPath = Path.Combine(_rootDirectory, RegistryFileName);
        _registryBackupPath = Path.Combine(_rootDirectory, RegistryBackupFileName);
    }

    public IReadOnlyList<AvatarEntry> Entries => _data.avatars;
    public IReadOnlyList<string> PrunedAvatarIds => _prunedAvatarIds;
    public string ActiveAvatarId => _data.activeAvatarId ?? string.Empty;
    public bool LegacyArmSettingsMigrated => _data.legacyArmSettingsMigrated;
    public string LastWarning { get; private set; } = string.Empty;

    public void Initialize()
    {
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_cacheDirectory);
        Directory.CreateDirectory(_temporaryDirectory);
        Directory.CreateDirectory(_trashDirectory);

        LastWarning = string.Empty;
        _prunedAvatarIds.Clear();
        CleanWorkingDirectory(_temporaryDirectory);

        bool loadedBackup = false;
        bool recoveredEmpty = false;
        if (!TryLoadData(_registryPath, out LibraryData loaded))
        {
            loadedBackup = TryLoadData(_registryBackupPath, out loaded);
            if (!loadedBackup)
            {
                loaded = new LibraryData();
                if (FileExistsStrict(_registryPath) || FileExistsStrict(_registryBackupPath))
                {
                    LastWarning = "아바타 목록 파일이 손상되어 빈 목록으로 복구했습니다.";
                    recoveredEmpty = true;
                }
            }
            else
            {
                LastWarning = "아바타 목록 백업을 사용해 복구했습니다.";
            }
        }

        _data = loaded ?? new LibraryData();
        RecoverReferencedTrashFiles();
        CleanUnreferencedTrashFiles();
        bool changed = NormalizeAndPrune();
        if (loadedBackup || recoveredEmpty || changed) SaveAtomic();
    }

    public bool TryGetEntry(string id, out AvatarEntry entry)
    {
        entry = _data.avatars.FirstOrDefault(item =>
            item != null && string.Equals(item.id, id, StringComparison.OrdinalIgnoreCase));
        return entry != null;
    }

    public string GetCachePath(string id)
    {
        string normalizedId = NormalizeId(id);
        return Path.Combine(_cacheDirectory, normalizedId + ".vrm");
    }

    public PendingImport StageImport(string sourcePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("선택한 파일 경로가 비어 있습니다.", nameof(sourcePath));
        if (!string.Equals(Path.GetExtension(sourcePath), ".vrm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(".vrm 파일만 등록할 수 있습니다.");

        string fullSourcePath = Path.GetFullPath(sourcePath);
        if (!FileExistsStrict(fullSourcePath))
            throw new FileNotFoundException("선택한 VRM 파일을 찾을 수 없습니다.", fullSourcePath);

        var sourceInfo = new FileInfo(fullSourcePath);
        if (sourceInfo.Length <= 0) throw new InvalidDataException("선택한 VRM 파일이 비어 있습니다.");

        Directory.CreateDirectory(_temporaryDirectory);
        string temporaryPath = Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            string id = CopyAndHash(fullSourcePath, temporaryPath, sourceInfo.Length, cancellationToken);
            string displayName = SanitizeDisplayName(Path.GetFileNameWithoutExtension(fullSourcePath), id);
            return new PendingImport(this, id, displayName, temporaryPath);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    public AvatarEntry CommitImportAndActivate(PendingImport pending, bool markLegacyArmSettingsMigrated = false)
    {
        ValidatePendingImport(pending);

        string targetPath = GetCachePath(pending.Id);
        string displacedPath = string.Empty;
        bool pendingMoved = false;
        LibraryData snapshot = CloneData(_data);

        try
        {
            if (FileExistsStrict(targetPath))
            {
                displacedPath = BuildTrashPath(pending.Id);
                File.Move(targetPath, displacedPath);
            }

            File.Move(pending.TemporaryPath, targetPath);
            pendingMoved = true;

            AvatarEntry entry = _data.avatars.FirstOrDefault(item =>
                item != null && string.Equals(item.id, pending.Id, StringComparison.OrdinalIgnoreCase));

            string now = DateTime.UtcNow.ToString("O");
            if (entry == null)
            {
                entry = new AvatarEntry
                {
                    id = pending.Id,
                    displayName = pending.DisplayName,
                    registeredUtc = now,
                    lastUsedUtc = now
                };
                _data.avatars.Add(entry);
            }
            else
            {
                entry.displayName = pending.DisplayName;
                if (string.IsNullOrWhiteSpace(entry.registeredUtc)) entry.registeredUtc = now;
                entry.lastUsedUtc = now;
            }

            _data.activeAvatarId = pending.Id;
            if (markLegacyArmSettingsMigrated) _data.legacyArmSettingsMigrated = true;
            SaveAtomic();
            pending.MarkFinished();
            TryDeleteFile(displacedPath);
            return entry;
        }
        catch
        {
            _data = snapshot;
            if (pendingMoved) TryDeleteFile(targetPath);
            TryRestoreFile(displacedPath, targetPath);
            throw;
        }
    }

    public void Activate(string id, bool markLegacyArmSettingsMigrated = false)
    {
        string normalizedId = NormalizeId(id);
        if (!TryGetEntry(normalizedId, out AvatarEntry entry))
            throw new InvalidOperationException("등록되지 않은 아바타입니다.");

        LibraryData snapshot = CloneData(_data);
        try
        {
            _data.activeAvatarId = normalizedId;
            if (markLegacyArmSettingsMigrated) _data.legacyArmSettingsMigrated = true;
            entry.lastUsedUtc = DateTime.UtcNow.ToString("O");
            SaveAtomic();
        }
        catch
        {
            _data = snapshot;
            throw;
        }
    }

    public void ClearActive()
    {
        if (string.IsNullOrEmpty(_data.activeAvatarId)) return;

        LibraryData snapshot = CloneData(_data);
        try
        {
            _data.activeAvatarId = string.Empty;
            SaveAtomic();
        }
        catch
        {
            _data = snapshot;
            throw;
        }
    }

    public bool Remove(string id)
    {
        string normalizedId = NormalizeId(id);
        int index = _data.avatars.FindIndex(item =>
            item != null && string.Equals(item.id, normalizedId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        string cachePath = GetCachePath(normalizedId);
        string trashPath = string.Empty;
        LibraryData snapshot = CloneData(_data);

        try
        {
            if (FileExistsStrict(cachePath))
            {
                trashPath = BuildTrashPath(normalizedId);
                File.Move(cachePath, trashPath);
            }

            _data.avatars.RemoveAt(index);
            if (string.Equals(_data.activeAvatarId, normalizedId, StringComparison.OrdinalIgnoreCase))
                _data.activeAvatarId = string.Empty;

            SaveAtomic();
            TryDeleteFile(trashPath);
            return true;
        }
        catch
        {
            _data = snapshot;
            TryRestoreFile(trashPath, cachePath);
            throw;
        }
    }

    public void MarkLegacyArmSettingsMigrated()
    {
        if (_data.legacyArmSettingsMigrated) return;

        LibraryData snapshot = CloneData(_data);
        try
        {
            _data.legacyArmSettingsMigrated = true;
            SaveAtomic();
        }
        catch
        {
            _data = snapshot;
            throw;
        }
    }

    public string ValidateRegisteredCache(string id, CancellationToken cancellationToken)
    {
        string normalizedId = NormalizeId(id);
        string path = GetCachePath(normalizedId);
        if (!FileExistsStrict(path))
            throw new FileNotFoundException("등록된 아바타 캐시 파일을 찾을 수 없습니다.", path);

        var file = new FileInfo(path);
        if (file.Length <= 0)
            throw new InvalidDataException("등록된 아바타 캐시 파일이 비어 있습니다.");

        string actualId = HashFile(path, file.Length, cancellationToken);
        if (!string.Equals(actualId, normalizedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("등록된 아바타 캐시 파일의 내용이 변경되었거나 손상되었습니다.");

        return path;
    }

    private bool NormalizeAndPrune()
    {
        bool changed = false;
        var prunedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_data.schemaVersion != CurrentSchemaVersion)
        {
            _data.schemaVersion = CurrentSchemaVersion;
            changed = true;
        }

        _data.avatars ??= new List<AvatarEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = _data.avatars.Count - 1; i >= 0; i--)
        {
            AvatarEntry entry = _data.avatars[i];
            if (entry == null || !IsValidId(entry.id) || !ids.Add(entry.id))
            {
                if (entry != null && IsValidId(entry.id)) prunedIds.Add(entry.id);
                _data.avatars.RemoveAt(i);
                changed = true;
                continue;
            }

            string normalizedId = entry.id.ToLowerInvariant();
            string normalizedDisplayName = SanitizeDisplayName(entry.displayName, normalizedId);
            if (!string.Equals(entry.id, normalizedId, StringComparison.Ordinal) ||
                !string.Equals(entry.displayName, normalizedDisplayName, StringComparison.Ordinal))
            {
                entry.id = normalizedId;
                entry.displayName = normalizedDisplayName;
                changed = true;
            }

            try
            {
                string cachePath = GetCachePath(entry.id);
                if (!FileExistsStrict(cachePath))
                {
                    prunedIds.Add(entry.id);
                    _data.avatars.RemoveAt(i);
                    changed = true;
                    continue;
                }

                var cacheInfo = new FileInfo(cachePath);
                if (cacheInfo.Length <= 0)
                {
                    TryDeleteFile(cachePath);
                    prunedIds.Add(entry.id);
                    _data.avatars.RemoveAt(i);
                    changed = true;
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                LastWarning = $"아바타 캐시 접근 권한을 확인하지 못했습니다: {exception.Message}";
            }
            catch (IOException exception)
            {
                LastWarning = $"아바타 캐시를 일시적으로 확인하지 못했습니다: {exception.Message}";
            }
        }

        if (!string.IsNullOrEmpty(_data.activeAvatarId))
        {
            string normalizedActiveId = _data.activeAvatarId.ToLowerInvariant();
            if (!string.Equals(_data.activeAvatarId, normalizedActiveId, StringComparison.Ordinal))
            {
                _data.activeAvatarId = normalizedActiveId;
                changed = true;
            }
            if (!_data.avatars.Any(item =>
                    item != null && string.Equals(item.id, _data.activeAvatarId, StringComparison.OrdinalIgnoreCase)))
            {
                _data.activeAvatarId = string.Empty;
                changed = true;
            }
        }

        foreach (string prunedId in prunedIds)
        {
            if (!_data.avatars.Any(item =>
                    item != null && string.Equals(item.id, prunedId, StringComparison.OrdinalIgnoreCase)))
            {
                _prunedAvatarIds.Add(prunedId.ToLowerInvariant());
            }
        }

        return changed;
    }

    private void SaveAtomic()
    {
        Directory.CreateDirectory(_rootDirectory);
        string temporaryRegistryPath = _registryPath + ".tmp";
        TryDeleteFile(temporaryRegistryPath);

        string json = JsonUtility.ToJson(_data, true);
        byte[] bytes = new UTF8Encoding(false).GetBytes(json);
        using (var stream = new FileStream(
                   temporaryRegistryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        if (FileExistsStrict(_registryPath))
        {
            File.Replace(temporaryRegistryPath, _registryPath, _registryBackupPath, true);
        }
        else
        {
            File.Move(temporaryRegistryPath, _registryPath);
        }
    }

    private static bool TryLoadData(string path, out LibraryData data)
    {
        data = null;
        if (!FileExistsStrict(path)) return false;

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return false;
            data = JsonUtility.FromJson<LibraryData>(json);
            return data != null && data.schemaVersion > 0 && data.avatars != null;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException ||
            exception is DirectoryNotFoundException ||
            exception is ArgumentException)
        {
            return false;
        }
    }

    private static string CopyAndHash(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.WriteThrough);
        using SHA256 sha256 = SHA256.Create();

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            sha256.TransformBlock(buffer, 0, read, null, 0);
            total += read;
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        destination.Flush(true);
        cancellationToken.ThrowIfCancellationRequested();

        if (total != expectedLength || source.Length != expectedLength)
            throw new IOException("VRM 파일이 복사 도중 변경되었습니다. 다시 시도하세요.");

        return BitConverter.ToString(sha256.Hash ?? Array.Empty<byte>())
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private static string HashFile(
        string path,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        using SHA256 sha256 = SHA256.Create();

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha256.TransformBlock(buffer, 0, read, null, 0);
            total += read;
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        cancellationToken.ThrowIfCancellationRequested();
        if (total != expectedLength || source.Length != expectedLength)
            throw new IOException("아바타 캐시 파일이 확인 도중 변경되었습니다. 다시 시도하세요.");

        return BitConverter.ToString(sha256.Hash ?? Array.Empty<byte>())
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private void ValidatePendingImport(PendingImport pending)
    {
        if (pending == null) throw new ArgumentNullException(nameof(pending));
        if (!ReferenceEquals(pending.Owner, this))
            throw new InvalidOperationException("다른 아바타 저장소에서 생성한 등록 작업입니다.");
        if (pending.IsFinished)
            throw new InvalidOperationException("이미 완료된 아바타 등록 작업입니다.");
        if (!FileExistsStrict(pending.TemporaryPath))
            throw new FileNotFoundException("임시 VRM 파일을 찾을 수 없습니다.", pending.TemporaryPath);
    }

    private string BuildTrashPath(string id)
    {
        Directory.CreateDirectory(_trashDirectory);
        return Path.Combine(_trashDirectory, NormalizeId(id) + "." + Guid.NewGuid().ToString("N") + ".trash");
    }

    private void CleanWorkingDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory)) TryDeleteFile(path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            LastWarning = $"아바타 임시 파일 일부를 정리하지 못했습니다: {exception.Message}";
        }
    }

    private void RecoverReferencedTrashFiles()
    {
        if (_data?.avatars == null || !Directory.Exists(_trashDirectory)) return;

        foreach (AvatarEntry entry in _data.avatars)
        {
            if (entry == null || !IsValidId(entry.id)) continue;
            string cachePath = GetCachePath(entry.id);

            try
            {
                if (FileExistsStrict(cachePath)) continue;

                string pattern = entry.id.ToLowerInvariant() + ".*.trash";
                foreach (string recoveryPath in Directory.EnumerateFiles(_trashDirectory, pattern)
                             .OrderByDescending(path => File.GetLastWriteTimeUtc(path)))
                {
                    try
                    {
                        var recoveryInfo = new FileInfo(recoveryPath);
                        if (recoveryInfo.Length <= 0 ||
                            !string.Equals(
                                HashFile(recoveryPath, recoveryInfo.Length, CancellationToken.None),
                                entry.id,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            TryDeleteFile(recoveryPath);
                            continue;
                        }

                        File.Move(recoveryPath, cachePath);
                        LastWarning = "중단된 아바타 캐시 작업을 SHA-256 확인 후 안전하게 복구했습니다.";
                        break;
                    }
                    catch (Exception exception) when (
                        exception is IOException ||
                        exception is UnauthorizedAccessException)
                    {
                        LastWarning = $"중단된 아바타 복구 파일을 일시적으로 확인하지 못했습니다: {exception.Message}";
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException)
            {
                LastWarning = $"중단된 아바타 캐시 작업을 복구하지 못했습니다: {exception.Message}";
            }
        }
    }

    private void CleanUnreferencedTrashFiles()
    {
        if (!Directory.Exists(_trashDirectory)) return;

        try
        {
            foreach (string trashPath in Directory.EnumerateFiles(_trashDirectory))
            {
                string fileName = Path.GetFileName(trashPath);
                int separatorIndex = fileName.IndexOf('.');
                string id = separatorIndex > 0 ? fileName.Substring(0, separatorIndex) : string.Empty;
                bool referenced = IsValidId(id) && TryGetEntry(id, out _);

                if (referenced)
                {
                    try
                    {
                        if (!FileExistsStrict(GetCachePath(id))) continue;
                    }
                    catch (Exception exception) when (
                        exception is IOException ||
                        exception is UnauthorizedAccessException)
                    {
                        LastWarning = $"참조 중인 아바타 복구 파일을 보존했습니다: {exception.Message}";
                        continue;
                    }
                }

                TryDeleteFile(trashPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException)
        {
            LastWarning = $"아바타 휴지통 일부를 확인하지 못해 보존했습니다: {exception.Message}";
        }
    }

    private static LibraryData CloneData(LibraryData source)
    {
        string json = JsonUtility.ToJson(source);
        return JsonUtility.FromJson<LibraryData>(json) ?? new LibraryData();
    }

    private static string NormalizeId(string id)
    {
        if (!IsValidId(id)) throw new InvalidDataException("아바타 등록 ID가 올바르지 않습니다.");
        return id.ToLowerInvariant();
    }

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 64) return false;
        foreach (char character in id)
        {
            bool digit = character is >= '0' and <= '9';
            bool lowerHex = character is >= 'a' and <= 'f';
            bool upperHex = character is >= 'A' and <= 'F';
            if (!digit && !lowerHex && !upperHex) return false;
        }

        return true;
    }

    private static string SanitizeDisplayName(string value, string id)
    {
        string fallback = IsValidId(id) ? $"아바타 {id.Substring(0, 8)}" : "아바타";
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var builder = new StringBuilder(Math.Min(value.Length, 48));
        foreach (char character in value)
        {
            if (builder.Length >= 48) break;
            if (!char.IsControl(character)) builder.Append(character);
        }

        string result = builder.ToString().Trim();
        return string.IsNullOrEmpty(result) ? fallback : result;
    }

    private static void TryRestoreFile(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrEmpty(sourcePath)) return;

        try
        {
            if (!FileExistsStrict(sourcePath)) return;
            TryDeleteFile(destinationPath);
            File.Move(sourcePath, destinationPath);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogError($"[AvatarLibraryStore] 파일 롤백 실패: {exception.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning($"[AvatarLibraryStore] 파일 정리 실패: {exception.Message}");
        }
    }

    private static bool FileExistsStrict(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
