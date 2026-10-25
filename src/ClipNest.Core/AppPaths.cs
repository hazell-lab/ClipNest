using System;
using System.IO;

namespace ClipNest.Core;

/// <summary>
/// where the app keeps its files. everything lives in one folder.
/// </summary>
public sealed class AppPaths
{
    public const string FolderName = "ClipNest";

    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
        Database = Path.Combine(root, "history.db");
        Images = Path.Combine(root, "images");
        Settings = Path.Combine(root, "settings.json");
        Logs = Path.Combine(root, "logs");
    }

    public string Root { get; }
    public string Database { get; }
    public string Images { get; }
    public string Settings { get; }
    public string Logs { get; }

    /// <summary>
    /// the normal location, a folder inside the local app data folder
    /// </summary>
    public static AppPaths ForCurrentUser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, FolderName));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(Logs);
    }
}
