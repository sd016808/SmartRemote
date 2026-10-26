namespace SmartRemote.Core
{
    public static class AppPaths
    {
        public static string InBaseDir(string name) => Path.Combine(AppContext.BaseDirectory, name);
    }
}
