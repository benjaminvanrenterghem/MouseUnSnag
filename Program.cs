/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Dale Roberts. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using MouseUnSnag.CommandLine;
using MouseUnSnag.Hooking;

namespace MouseUnSnag
{

    public class Program
    {
        private IntPtr _llMouseHookhand = IntPtr.Zero;
        private Point _lastMouse = new Point(0, 0);
        private int _nJumps;

        private HookHandler _hookHandler;
        

        /// <summary>
        /// Command Line Options
        /// </summary>
        internal Options Options { get; private set; }


        // CheckJumpCursor() returns TRUE, ONLY if the cursor is "stuck". By "stuck" we
        // specifically mean that the user is trying to move the mouse beyond the boundaries of
        // the screen currently containing the cursor. This is determined when the *current*
        // cursor position does not equal the *previous* mouse position. If there is another
        // adjacent screen (or a "wrap around" screen), then we can consider moving the mouse
        // onto that screen.
        //
        // Note that this is ENTIRELY a *GEOMETRIC* method. Screens are "rectangles", and the
        // cursor and mouse are "points." The mouse/cursor hardware interaction (obtaining
        // current mouse and cursor information) is handled in routines further below, and any
        // Screen changes are handled by the DisplaySettingsChanged event. There are no
        // hardware or OS/Win32 references or interactions here.
        private bool CheckJumpCursor(Point mouse, Point cursor, out Point NewCursor)
        {
            NewCursor = cursor; // Default is to not move cursor.

            // Gather pertinent information about cursor, mouse, screens.
            var lastScreen = SnagScreen.WhichScreen(_lastMouse);
            var cursorScreen = SnagScreen.WhichScreen(cursor);
            var mouseScreen = SnagScreen.WhichScreen(mouse);
            var isStuck = (cursor != _lastMouse) && (mouseScreen != cursorScreen) || (mouseScreen != lastScreen);
            var stuckDirection = GeometryUtil.OutsideDirection(cursorScreen.R, mouse);

            Debug.WriteLine($" StuckDirection/Distance{stuckDirection}/{GeometryUtil.OutsideDistance(cursorScreen.R, mouse)} " +
                             $"cur_mouse:{mouse}  prev_mouse:{_lastMouse} ==? cursor:{cursor} (OnMon#{cursorScreen}/{mouseScreen})  " +
                             $"#UnSnags {_nJumps}   {(isStuck ? "--STUCK--" : "         ")}   ");

            _lastMouse = mouse;

            // Let caller know we did NOT jump the cursor.
            if (!isStuck)
                return false;

            var jumpScreen = SnagScreen.ScreenInDirection(stuckDirection, cursorScreen.R);

            // If the mouse "location" (which can take on a value beyond the current
            // cursor screen) has a value, then it is "within" another valid screen
            // bounds, so just jump to it!
            if (mouseScreen != null)
            {
                if (!Options.Unstick)
                    return false;

                if (lastScreen != mouseScreen && lastScreen != null)
                {
                    var newCursor = mouse;
                    newCursor.Y = newCursor.Y * mouseScreen.EffectiveDpi / lastScreen.EffectiveDpi;
                    Debug.WriteLine($"R: {newCursor}, {lastScreen.EffectiveDpi}>{mouseScreen.EffectiveDpi}");
                    NewCursor = newCursor;
                }
                else
                {
                    NewCursor = mouse;
                }
            }
            else if (jumpScreen != null)
            {
                if (!Options.Jump)
                    return false;
                NewCursor = jumpScreen.R.ClosestBoundaryPoint(cursor);
            }
            else if (stuckDirection.X != 0)
            {
                if (!Options.Wrap)
                    return false;

                var wrapScreen = SnagScreen.WrapScreen(stuckDirection, cursor);
                var wrapPoint = new Point(
                    stuckDirection.X == 1 ? wrapScreen.R.Left : wrapScreen.R.Right - 1, cursor.Y);

                // Don't wrap cursor if jumping is disabled and it would need to jump.
                if (!Options.Jump && !wrapScreen.R.Contains(wrapPoint))
                    return false;

                NewCursor = wrapScreen.R.ClosestBoundaryPoint(wrapPoint);
            }
            else
            {
                return false;
            }

            ++_nJumps;
            Debug.WriteLine("\n -- JUMPED!!! --");
            return true;
        }

        // Called whenever the mouse moves. This routine leans entirely on the
        // CheckJumpCursor() routine to see if there is any need to "mess with" the cursor
        // position, to make it jump from one monitor to another.
        private IntPtr LlMouseHookCallback(int nCode, uint wParam, IntPtr lParam)
        {
            if ((nCode < 0) || (wParam != NativeMethods.WmMousemove) || _updatingDisplaySettings)
                goto ExitToNextHook;

            var hookStruct = (NativeMethods.Msllhookstruct)Marshal.PtrToStructure(lParam, typeof(NativeMethods.Msllhookstruct));
            var mouse = hookStruct.pt;

            // If we jump the cursor, then we return 1 here to tell the OS that we
            // have handled the message, so it doesn't call SetCursorPos() right
            // after we do, and "undo" our call to SetCursorPos().
            if (NativeMethods.GetCursorPos(out var cursor) && CheckJumpCursor(mouse, cursor, out var newCursor))
            {
                NativeMethods.SetCursorPos(newCursor);
                return (IntPtr) 1;
            }

            ExitToNextHook:
            return NativeMethods.CallNextHookEx(_llMouseHookhand, nCode, wParam, lParam);
        }

        

        // Need to explicitly keep a reference to this, so it does not get "garbage collected."
        private NativeMethods.HookProc _mouseHookDelegate;

        

        [STAThread]
        public static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(defaultValue: false);

            // Make sure the MouseUnSnag.exe has only one instance running at a time.
            using (new Mutex(initiallyOwned: true, "__MouseUnSnag_EXE__", out var createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("MouseUnSnag is already running!! Quitting this instance...", "MouseUnSnag", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 2;
                }
                TrySetDpiAwareness();
                new Program().Run(new CommandLineParser().Decode(Environment.GetCommandLineArgs().Skip(1)));
            }

            return 0;
        }


        private static void TrySetDpiAwareness()
        {
            try
            {
                NativeMethods.SetProcessDpiAwareness(NativeMethods.ProcessDpiAwareness.ProcessPerMonitorDpiAware);
            }
            catch (DllNotFoundException)
            {
                // DPI Awareness API is not available on older OS's, but they work in
                // physical pixels anyway, so we just ignore if the call fails.
                Debug.WriteLine("No SHCore.DLL. No problem.");
            }
        }

        private void Run(Options options)
        {
            Options = options;

            SnagScreen.Init(Screen.AllScreens);
            Debug.WriteLine(SnagScreen.GetScreenInformation());

            // Get notified of any screen configuration changes.
            SystemEvents.DisplaySettingsChanged += Event_DisplaySettingsChanged;

            // Keep a reference to the delegate, so it does not get garbage collected.
            _mouseHookDelegate = LlMouseHookCallback;
            _hookHandler = new HookHandler();
            _llMouseHookhand = _hookHandler.SetHook(NativeMethods.WhMouseLl, _mouseHookDelegate);

            
            using (var ctx = new MyCustomApplicationContext(options))
            {
                // This is the one that runs "forever" while the application is alive, and handles
                // events, etc. This application is ABSOLUTELY ENTIRELY driven by the LLMouseHook
                // and DisplaySettingsChanged events.
                Application.Run(ctx);
            }
            

            Debug.WriteLine("Exiting!!!");
            HookHandler.UnsetHook(ref _llMouseHookhand);
            SystemEvents.DisplaySettingsChanged -= Event_DisplaySettingsChanged;
        }

        private volatile bool _updatingDisplaySettings;
        private void Event_DisplaySettingsChanged(object sender, EventArgs e)
        {
            _updatingDisplaySettings = true;
            Debug.WriteLine("\nDisplay Settings Changed...");
            SnagScreen.Init(Screen.AllScreens);
            Debug.WriteLine(SnagScreen.GetScreenInformation());
            _updatingDisplaySettings = false;
        }

    }
}
