using System.IO;
using WinIDM.App.Services;

namespace WinIDM.App.Tests;

/// <summary>IDM+ bookmark folders: move, folders-first order, and old bookmark files without a folder still load.</summary>
public class BookmarkFolderTests
{
    [Fact]
    public void MoveToFolder_AndFoldersFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winidm-bm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "bookmarks.json");
            var store = new BookmarkStore(path);
            store.Add("A", "https://a.example/");
            store.Add("B", "https://b.example/");
            store.MoveToFolder("https://a.example/", " Work ");

            Assert.Equal("https://a.example/", store.Ordered(foldersFirst: true)[0].Url);
            Assert.Equal("https://b.example/", store.Ordered(foldersFirst: false)[0].Url);

            var reloaded = new BookmarkStore(path);
            Assert.Equal("Work", reloaded.All.Single(b => b.Url == "https://a.example/").Folder);

            reloaded.MoveToFolder("https://a.example/", "");
            Assert.Null(reloaded.All.Single(b => b.Url == "https://a.example/").Folder);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OldFileWithoutFolder_StillLoads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winidm-bm-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "bookmarks.json");
            File.WriteAllText(path, "[{\"Title\":\"Old\",\"Url\":\"https://old.example/\",\"Added\":\"2025-01-01T00:00:00+00:00\"}]");

            var store = new BookmarkStore(path);
            Assert.Null(Assert.Single(store.All).Folder);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
