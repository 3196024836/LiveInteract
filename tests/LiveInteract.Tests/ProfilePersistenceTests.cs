using System.Collections.Concurrent;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

public sealed class ProfilePersistenceTests
{
    /// <summary>界面保存与其他调用不能争用同一个 .tmp，任何一次成功保存都必须是完整 JSON。</summary>
    [Fact]
    public void ConcurrentProfileSavesAreCompleteAndLeaveNoTemporaryFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "LiveInteract-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "rules.json");
            var errors = new ConcurrentQueue<Exception>();
            Parallel.For(0, 80, i =>
            {
                try { ProfileStore.Save(file, new RuleProfile { Name = "配置" + i }); }
                catch (Exception error) { errors.Enqueue(error); }
            });
            Assert.Empty(errors);
            Assert.StartsWith("配置", ProfileStore.Read(File.ReadAllText(file), false).Name);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
