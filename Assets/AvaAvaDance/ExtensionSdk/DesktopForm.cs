using System;
using System.Collections.Generic;

namespace Ava.Extensions
{
    // Optional service implemented by the native desktop hosts. Callbacks run on
    // the editor thread. Change Revision when a field starts editing a new target.
    public interface IDesktopFormHost
    {
        bool SupportsDesktopForms { get; }
        IDisposable OpenForm(DesktopForm form);
        void ConfirmForm(string title, string message, Action accept);
        string PickFile(string title, string extension, bool save);
    }

    public sealed class DesktopForm
    {
        public string Name, Title;
        public int Width, Height;
        public long Revision;
        public readonly List<DesktopFormControl> Controls = new List<DesktopFormControl>();
        public Action<int, int> Layout;
        public Action<int, string, string, int> Input;
        public Action<string> DropFile;
        public Action Closed;
    }

    public sealed class DesktopFormControl
    {
        public int Id, X, Y, Width, Height, MaxLength;
        public string Kind, Name, Text = "";
        public bool Enabled = true, Hit = true;
        public bool Checked, ReadOnly;
        public string[] Choices, ChoiceLabels;
    }
}
