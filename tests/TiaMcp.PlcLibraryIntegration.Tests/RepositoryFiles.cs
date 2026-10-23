internal static class RepositoryFiles
{
    internal static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TiaMcp.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the test source snapshot containing TiaMcp.slnx.");
    }
}
