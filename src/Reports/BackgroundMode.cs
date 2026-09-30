using System;
using System.Runtime.InteropServices;

namespace NomisKitchen.Reports
{
    internal static class BackgroundMode
    {
        const int ThreadModeBackgroundBegin = 0x00010000;
        const int ThreadModeBackgroundEnd = 0x00020000;
        const int ThreadPriorityLowest = -2;
        const int ThreadPriorityNormal = 0;

        internal enum Priority { Unchanged, Background, Lowest }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetThreadPriority(IntPtr thread, int priority);

        internal static Priority Enter()
        {
            try
            {
                if (SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin)) return Priority.Background;
                if (SetThreadPriority(GetCurrentThread(), ThreadPriorityLowest)) return Priority.Lowest;
            }
            catch { }
            return Priority.Unchanged;
        }

        internal static void Exit(Priority priority)
        {
            try
            {
                if (priority == Priority.Background) SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
                else if (priority == Priority.Lowest) SetThreadPriority(GetCurrentThread(), ThreadPriorityNormal);
            }
            catch { }
        }
    }
}
