namespace VRChatPhotoSync.Tests;

public sealed class UploadQueueTests
{
    private const string A = @"D:\Photos\VRChat\2026-11\VRChat_2026-11-21_21-34-55.168_3840x2160.png";
    private const string B = @"D:\Photos\VRChat\2026-11\VRChat_2026-11-21_21-36-10.001_3840x2160.png";
    private const string ARenamed = @"D:\Photos\VRChat\2026-11\VRChat_2026-11-21_21-34-55.168_3840x2160_.png";

    private static List<string> Drain(UploadQueue queue)
    {
        var paths = new List<string>();
        while (queue.TryDequeue(out var p))
            paths.Add(p);
        return paths;
    }

    [Fact]
    public void Deduplicates_By_Path_Ignoring_Case()
    {
        var queue = new UploadQueue();
        Assert.True(queue.Enqueue(A));
        Assert.False(queue.Enqueue(A.ToUpperInvariant()));
        Assert.Equal(1, queue.Count);
        Assert.Equal([A], Drain(queue));
        Assert.True(queue.Enqueue(A)); // can be queued again once taken
    }

    [Fact]
    public void Rename_Keeps_The_Place_In_The_Queue()
    {
        var queue = new UploadQueue();
        queue.Enqueue(A);
        queue.Enqueue(B);

        Assert.True(queue.Rename(A, ARenamed));

        Assert.False(queue.Contains(A));
        Assert.True(queue.Contains(ARenamed));
        Assert.Equal(2, queue.Count);
        Assert.Equal([ARenamed, B], Drain(queue));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Rename_Onto_A_Queued_Path_Drops_The_Old_Entry()
    {
        var queue = new UploadQueue();
        queue.Enqueue(A);
        queue.Enqueue(ARenamed);

        Assert.True(queue.Rename(A, ARenamed));

        Assert.Equal(1, queue.Count);
        Assert.Equal([ARenamed], Drain(queue));
    }

    [Fact]
    public void Rename_Of_A_Path_That_Is_Not_Queued_Returns_False()
    {
        var queue = new UploadQueue();
        queue.Enqueue(B);
        Assert.False(queue.Rename(A, ARenamed));
        Assert.Equal([B], Drain(queue));
    }

    [Fact]
    public void Old_Path_Can_Be_Queued_Again_After_A_Rename()
    {
        var queue = new UploadQueue();
        queue.Enqueue(A);
        queue.Rename(A, ARenamed);
        Assert.True(queue.Enqueue(A));
        Assert.Equal([ARenamed, A], Drain(queue));
    }

    [Fact]
    public async Task ReadAll_Skips_Dropped_Entries()
    {
        var queue = new UploadQueue();
        queue.Enqueue(A);
        queue.Enqueue(ARenamed);
        queue.Rename(A, ARenamed);
        queue.Complete();

        var paths = new List<string>();
        await foreach (var p in queue.ReadAllAsync())
            paths.Add(p);
        Assert.Equal([ARenamed], paths);
    }
}
