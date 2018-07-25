﻿namespace LostTech.Stack.ScreenTracking
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Interop;
    using LostTech.Stack.WindowManagement;
    using LostTech.Windows;
    using Microsoft.Win32;
    using PInvoke;

    public class ScreenLayout: Window
    {
        HwndSource handle;

        public ScreenLayout()
        {
            this.DataContextChanged += this.OnDataContextChanged;
            this.Loaded += this.OnLoaded;
            // this also makes window to be visible on all virtual desktops
            this.SetIsListedInTaskSwitcher(false);
            SystemEvents.SessionSwitch += this.OnSessionSwitch;
        }

        public Task<bool> SetLayout(FrameworkElement layout) {
            layout.Width = layout.Height = double.NaN;
            this.Content = layout;
            var readiness = new TaskCompletionSource<bool>();

            layout.Loaded += delegate {
                if (this.windowPositioned) {
                    this.ready.TrySetResult(true);
                    readiness.TrySetResult(true);
                }
            };
            layout.Unloaded += delegate { readiness.TrySetCanceled(); };
            return readiness.Task;
        }

        public FrameworkElement Layout => this.Content as FrameworkElement;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            this.handle = (HwndSource)PresentationSource.FromVisual(this);
            // ReSharper disable once PossibleNullReferenceException
            this.handle.AddHook(this.OnWindowMessage);
            this.SetScreen(this.Screen);
        }

        readonly TaskCompletionSource<bool> loaded = new TaskCompletionSource<bool>();
        void OnLoaded(object sender, EventArgs e) {
            this.AdjustToScreenWhenIdle();
            this.loaded.TrySetResult(true);
        }

        protected override void OnClosed(EventArgs e)
        {
            this.handle?.RemoveHook(this.OnWindowMessage);
            this.handle = null;
            base.OnClosed(e);
        }

        public bool IsHandleInitialized => this.handle != null;

        private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch ((User32.WindowMessage) msg) {
            case User32.WindowMessage.WM_SETTINGCHANGE:
                string reason = "OnWindowMessage:" + (lParam == IntPtr.Zero ? null : Marshal.PtrToStringAuto(lParam));
                this.AdjustToScreenWhenIdle(reason);
                break;
            }
            return IntPtr.Zero;
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e) {
            switch (e.Reason) {
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.SessionUnlock:
                this.AdjustToScreenWhenIdle();
                break;
            }
        }

        Win32Screen lastScreen;
        public Win32Screen Screen => this.ViewModel?.Screen;

        public IScreenLayoutViewModel ViewModel {
            get => (IScreenLayoutViewModel)this.DataContext;
            set => this.DataContext = value;
        }

        void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
            if (e.OldValue is IScreenLayoutViewModel viewModel)
                viewModel.PropertyChanged -= this.OnViewModelPropertyChanged;
            if (e.NewValue is IScreenLayoutViewModel newViewModel) {
                newViewModel.PropertyChanged += this.OnViewModelPropertyChanged;
                this.SetScreen(newViewModel.Screen);
            }
        }

        void SetScreen(Win32Screen newScreen) {
            if (this.lastScreen == newScreen)
                return;

            if (this.lastScreen != null)
                this.lastScreen.PropertyChanged -= this.OnScreenPropertyChanged;
            this.lastScreen = newScreen;
            if (this.lastScreen != null) {
                this.lastScreen.PropertyChanged += this.OnScreenPropertyChanged;
                this.AdjustToScreenWhenIdle();
            }
        }

        void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e) {
            switch (e.PropertyName) {
            case nameof(IScreenLayoutViewModel.Screen):
                this.SetScreen(this.ViewModel?.Screen);
                break;
            }
        }

        void OnScreenPropertyChanged(object sender, PropertyChangedEventArgs e) {
            switch (e.PropertyName) {
            case nameof(this.Screen.WorkingArea):
            case nameof(this.Screen.TransformFromDevice):
                this.AdjustToScreenWhenIdle();
                break;
            }
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);

            Debug.WriteLine($"{this.Screen} DPI: {oldDpi.PixelsPerInchX} -> {newDpi.PixelsPerInchX}");

            this.AdjustToScreenWhenIdle();
        }

        Task idleAdjustDelay;
        async void AdjustToScreenWhenIdle([CallerMemberName] string callerName = null) {
            var delay = Task.Delay(millisecondsDelay: 500);
            this.idleAdjustDelay = delay;
            await Task.WhenAll(delay, this.loaded.Task);
            if (delay != this.idleAdjustDelay)
                return;
            if (this.IsLoaded) {
                this.AdjustToScreen();
                Debug.WriteLine($"adjust caused by {callerName}");
            }
        }

        readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>();
        bool windowPositioned = false;
        /// <summary>
        /// Completes, when this instance is adjusted to the screen, and some layout is loaded
        /// </summary>
        public Task Ready => this.ready.Task;
        async void AdjustToScreen()
        {
            for (int retry = 0; retry < 8; retry++) {
                if (this.Screen == null || !this.IsHandleInitialized)
                    return;

                double opacity = this.Opacity;
                var visibility = this.Visibility;
                if (visibility != Visibility.Visible) {
                    this.Opacity = 0;
                    try {
                        this.Show();
                    } catch (InvalidOperationException) {
                        await Task.Delay(400);
                        continue;
                    }
                }

                Debug.WriteLine($"adjusting {this.Title} to {this.Screen.WorkingArea}");
                if (!this.Screen.IsActive || !await WindowExtensions.AdjustToClientArea(this, this.Screen)) {
                    await Task.Delay(400);
                    continue;
                }

                try {
                    this.Visibility = visibility;
                } catch (InvalidOperationException) {
                    await Task.Delay(400);
                    continue;
                }

                this.Opacity = opacity;
                this.windowPositioned = true;
                this.InvalidateMeasure();
                this.Layout?.InvalidateMeasure();

                if (Math.Abs((double)(this.RenderSize.Width - this.Width)) < 10
                    && Math.Abs((double)(this.RenderSize.Height - this.Height)) < 10) {
                    if (this.Layout != null) {
                        await Task.Yield();
                        this.ready.TrySetResult(true);
                    }
                    return;
                }
                else
                    await Task.Delay(400);
            }
        }

        public bool TryShow() {
            if (!this.IsHandleInitialized)
                return false;
            try {
                this.Show();
                return true;
            } catch (InvalidOperationException) {
                return false;
            }
        }

        public bool TryHide() {
            if (!this.IsHandleInitialized)
                return false;
            try {
                this.Hide();
                return true;
            } catch (InvalidOperationException) {
                return false;
            }
        }
    }

    static class ScreenLayoutExtensions
    {
        public static IEnumerable<ScreenLayout> Active(this IEnumerable<ScreenLayout> layouts)
            => layouts.Where(layout => layout.Screen.IsActive);
    }
}