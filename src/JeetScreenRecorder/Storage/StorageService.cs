namespace JeetScreenRecorder.Storage;

public sealed class StorageService : IStorageService
{
    public long GetFreeSpaceBytes(string folder)
    {
        Directory.CreateDirectory(folder);
        var root = Path.GetPathRoot(Path.GetFullPath(folder))!;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public bool HasEnoughSpace(string folder, long requiredBytes) =>
        GetFreeSpaceBytes(folder) >= requiredBytes;
}
