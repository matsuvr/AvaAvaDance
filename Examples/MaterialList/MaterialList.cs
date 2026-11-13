using System;
using System.Linq;
using Ava.Extensions;

namespace Tools
{
    public sealed class MaterialList : IAvaPlugin
    {
        IPluginContext host;
        IDisposable registration;
        public void Register(IPluginRegistrar commands)
        {
            registration=commands.AddCommand("example.material-list.show","プラグイン(&E)","材質一覧",
                ()=>host.Report(MaterialLabels.Summary.Format(host.Materials().Select(m=>m.ActorName+" / "+m.MaterialName))));
        }
        public void Activate(IPluginContext context) {host=context;}
        public void Deactivate() {host=null;}
        public void Dispose() {registration?.Dispose();}
    }
}
