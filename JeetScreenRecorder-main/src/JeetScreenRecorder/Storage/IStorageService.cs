namespace JeetScreenRecorder.Storage;

public interface IStorageService
{
    long GetFreeSpaceBytes(string folder);
    bool HasEnoughSpace(string folder, long requiredBytes);
}
