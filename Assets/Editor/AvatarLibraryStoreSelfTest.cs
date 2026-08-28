#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace VAS.Editor
{
    public static class AvatarLibraryStoreSelfTest
    {
        [MenuItem("VAS/테스트/아바타 목록 저장소 자체검사")]
        public static void Run()
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string testRoot = Path.GetFullPath(Path.Combine(
                temporaryRoot,
                "VAS-AvatarLibrary-SelfTest-" + Guid.NewGuid().ToString("N")));

            if (!testRoot.StartsWith(
                    temporaryRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("자체검사 임시 경로가 안전하지 않습니다.");
            }

            try
            {
                Directory.CreateDirectory(testRoot);
                string persistentPath = Path.Combine(testRoot, "PersistentData");
                string sourcePath = Path.Combine(testRoot, "테스트 아바타.vrm");
                File.WriteAllBytes(sourcePath, Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray());

                var store = new AvatarLibraryStore(persistentPath);
                store.Initialize();
                Require(store.Entries.Count == 0, "최초 목록은 비어 있어야 합니다.");
                Require(string.IsNullOrEmpty(store.ActiveAvatarId), "최초 활성 ID는 비어 있어야 합니다.");

                string firstId;
                using (AvatarLibraryStore.PendingImport pending = store.StageImport(sourcePath, CancellationToken.None))
                {
                    firstId = pending.Id;
                    store.CommitImportAndActivate(pending, markLegacyArmSettingsMigrated: true);
                }

                Require(store.Entries.Count == 1, "첫 등록 항목이 생성되지 않았습니다.");
                Require(store.ActiveAvatarId == firstId, "첫 등록 항목이 활성화되지 않았습니다.");
                Require(store.LegacyArmSettingsMigrated, "레거시 설정 이전 완료 상태가 저장되지 않았습니다.");
                Require(File.Exists(store.GetCachePath(firstId)), "등록 캐시가 생성되지 않았습니다.");

                using (AvatarLibraryStore.PendingImport duplicate = store.StageImport(sourcePath, CancellationToken.None))
                {
                    Require(duplicate.Id == firstId, "동일 파일의 SHA-256 ID가 달라졌습니다.");
                    store.CommitImportAndActivate(duplicate);
                }

                Require(store.Entries.Count == 1, "동일 파일을 중복 등록했습니다.");
                Require(File.Exists(sourcePath), "앱 등록 과정에서 사용자 원본을 삭제했습니다.");

                string registryPath = Path.Combine(persistentPath, "AvatarLibrary", "avatar-library.json");
                string upperId = firstId.ToUpperInvariant();
                string registryJson = File.ReadAllText(registryPath);
                registryJson = registryJson.Replace(firstId, upperId);
                registryJson = registryJson.Replace(
                    "\"displayName\": \"테스트 아바타\"",
                    "\"displayName\": \"   \"");
                File.WriteAllText(registryPath, registryJson);

                var normalizedStore = new AvatarLibraryStore(persistentPath);
                normalizedStore.Initialize();
                string normalizedRegistryJson = File.ReadAllText(registryPath);
                Require(normalizedStore.Entries[0].Id == firstId, "등록 ID를 소문자로 정규화하지 못했습니다.");
                Require(
                    normalizedStore.Entries[0].DisplayName == "아바타 " + firstId.Substring(0, 8),
                    "빈 표시명을 안전한 기본 이름으로 정규화하지 못했습니다.");
                Require(!normalizedRegistryJson.Contains(upperId), "정규화된 등록 ID를 목록 파일에 저장하지 않았습니다.");
                Require(
                    normalizedRegistryJson.Contains("아바타 " + firstId.Substring(0, 8)),
                    "정규화된 표시명을 목록 파일에 저장하지 않았습니다.");
                store = normalizedStore;

                string trashDirectory = Path.Combine(persistentPath, "AvatarLibrary", "Trash");
                Directory.CreateDirectory(trashDirectory);
                string validTrashPath = Path.Combine(trashDirectory, firstId + ".interrupted.trash");
                string invalidTrashPath = Path.Combine(trashDirectory, firstId + ".newer-corrupted.trash");
                File.Move(store.GetCachePath(firstId), validTrashPath);
                File.WriteAllBytes(invalidTrashPath, new byte[] { 1, 2, 3, 4 });
                File.SetLastWriteTimeUtc(invalidTrashPath, DateTime.UtcNow.AddMinutes(1));
                var interruptedStore = new AvatarLibraryStore(persistentPath);
                interruptedStore.Initialize();
                Require(
                    File.Exists(interruptedStore.GetCachePath(firstId)),
                    "중단된 교체 작업의 참조 캐시를 휴지통에서 복구하지 못했습니다.");
                Require(interruptedStore.Entries.Count == 1, "중단 복구 후 등록 항목이 사라졌습니다.");
                Require(!File.Exists(invalidTrashPath), "SHA-256이 다른 휴지통 복구 후보를 폐기하지 않았습니다.");
                Require(
                    interruptedStore.ValidateRegisteredCache(firstId, CancellationToken.None) ==
                    interruptedStore.GetCachePath(firstId),
                    "SHA-256이 일치하는 휴지통 후보를 복구하지 않았습니다.");
                store = interruptedStore;

                Require(store.Remove(firstId), "등록 제거가 실패했습니다.");
                Require(store.Entries.Count == 0, "등록 제거 후 목록이 비어 있지 않습니다.");
                Require(string.IsNullOrEmpty(store.ActiveAvatarId), "현재 아바타 제거 후 활성 ID가 남았습니다.");
                Require(!File.Exists(store.GetCachePath(firstId)), "등록 제거 후 앱 캐시가 남았습니다.");
                Require(File.Exists(sourcePath), "등록 제거 과정에서 사용자 원본을 삭제했습니다.");

                string missingCacheId;
                using (AvatarLibraryStore.PendingImport pending = store.StageImport(sourcePath, CancellationToken.None))
                {
                    missingCacheId = pending.Id;
                    store.CommitImportAndActivate(pending);
                }

                File.Delete(store.GetCachePath(missingCacheId));
                var recoveredStore = new AvatarLibraryStore(persistentPath);
                recoveredStore.Initialize();
                Require(recoveredStore.Entries.Count == 0, "누락된 캐시 항목을 시작 시 제거하지 않았습니다.");
                Require(string.IsNullOrEmpty(recoveredStore.ActiveAvatarId), "누락된 현재 캐시의 활성 ID가 남았습니다.");
                Require(
                    recoveredStore.PrunedAvatarIds.Count == 1 && recoveredStore.PrunedAvatarIds[0] == missingCacheId,
                    "시작 시 제거한 아바타 ID를 로컬 설정 정리 대상으로 보고하지 않았습니다.");

                File.WriteAllBytes(sourcePath, Enumerable.Range(0, 2048).Select(index => (byte)(index % 239)).ToArray());
                string corruptedId;
                using (AvatarLibraryStore.PendingImport pending = recoveredStore.StageImport(sourcePath, CancellationToken.None))
                {
                    corruptedId = pending.Id;
                    recoveredStore.CommitImportAndActivate(pending);
                }

                File.WriteAllBytes(recoveredStore.GetCachePath(corruptedId), new byte[] { 1, 2, 3, 4 });
                bool corruptionDetected = false;
                try
                {
                    recoveredStore.ValidateRegisteredCache(corruptedId, CancellationToken.None);
                }
                catch (InvalidDataException)
                {
                    corruptionDetected = true;
                }

                Require(corruptionDetected, "SHA-256 불일치 캐시를 손상으로 감지하지 못했습니다.");
                Debug.Log("[VAS Test] 아바타 목록 저장소 자체검사 통과.");
            }
            finally
            {
                if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("[VAS Test] " + message);
        }
    }
}
#endif
