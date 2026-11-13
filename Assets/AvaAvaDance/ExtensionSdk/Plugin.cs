using System;
using System.Collections.Generic;

namespace Ava.Extensions
{
    public interface IAvaPlugin : IDisposable
    {
        void Register(IPluginRegistrar registrar);
        void Activate(IPluginContext context);
        void Deactivate();
    }

    public interface IAsyncShutdown
    {
        System.Threading.Tasks.Task CloseAsync();
    }
    public interface IFileProcessor
    {
        string[] Process(string requestFile,string outputDirectory,System.Threading.CancellationToken cancellation);
    }
    public interface IProjectStorage
    {
        string[] Blobs();
        byte[] ReadBlob(string id);
        void WriteBlock(byte[] state,IReadOnlyDictionary<string,byte[]> blobChanges);
    }
    public interface IPluginRegistrar
    {
        IDisposable AddShortcut(string command,string chord);
        IDisposable AddCommand(string id, string menu, string caption, Action execute, Func<bool> available = null, Func<bool> check = null);
    }

    // Project access runs on the editor thread. Post schedules work from a window or worker.
    public interface IWindowSettings { string WindowSettingsDirectory {get;} }
    public interface IWindowHost
    {
        IntPtr MainWindow { get; }
        void Post(Action operation);
        IDisposable KeepActive();
        void Report(string message);
    }
    public interface IPluginContext : IWindowHost
    {
        string PackageDirectory { get; }
        string SettingsDirectory { get; }
        long DocumentGeneration { get; }
        IDisposable ObserveProjects(Action<string> opened,Action<string> saved);
        MaterialSlot[] Materials();
        IDisposable AddSurfaceProvider(ISurfaceProvider provider);
        RigProfile[] ReadRigs(string path);
        byte[] ReadState();
        void WriteState(byte[] bytes);
        bool CanExecute(string command);
        bool Execute(string command);
    }
}
