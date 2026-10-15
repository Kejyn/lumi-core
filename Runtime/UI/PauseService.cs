using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Lumivale.Core.Bootstrap;
using Lumivale.Core.Input;
using UnityEngine;

namespace Lumivale.Core.UI
{
    /// <summary>Coordinates a pause modal and Input context; time scaling is explicitly opt-in.</summary>
    public sealed class PauseService : ILumiService
    {
        private static readonly string[] ServiceDependencies = { "input", "ui" };
        private readonly string screenId;
        private readonly bool controlTimeScale;
        private InputService input;
        private UiService ui;
        private InputContext previousContext;
        private float previousTimeScale;
        private bool restoring;

        public PauseService(string pauseScreenId, bool controlTimeScale = false)
        {
            if (string.IsNullOrWhiteSpace(pauseScreenId))
            {
                throw new ArgumentException("A pause screen id is required.", nameof(pauseScreenId));
            }

            screenId = pauseScreenId;
            this.controlTimeScale = controlTimeScale;
        }

        public string Id => "pause";
        public IReadOnlyCollection<string> Dependencies => Array.AsReadOnly(ServiceDependencies);
        public bool IsPaused { get; private set; }

        public Task InitializeAsync(ServiceContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (ui != null)
            {
                return Task.CompletedTask;
            }

            input = context.Get<InputService>();
            ui = context.Get<UiService>();
            if (!input.IsInitialized || !ui.IsInitialized)
            {
                input = null;
                ui = null;
                throw new InvalidOperationException("Pause requires initialized Input and UI services.");
            }

            ui.ScreenClosed += OnScreenClosed;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            try
            {
                Restore(true);
            }
            finally
            {
                if (ui != null)
                {
                    ui.ScreenClosed -= OnScreenClosed;
                }

                ui = null;
                input = null;
            }

            return Task.CompletedTask;
        }

        public void Pause()
        {
            EnsureInitialized();
            if (IsPaused || restoring)
            {
                return;
            }

            if (ui.IsOpen(screenId))
            {
                throw new InvalidOperationException("The pause screen is already open outside PauseService.");
            }

            // Resolve first: a missing screen must not change input or time.
            ui.GetScreen(screenId);
            previousContext = input.CurrentContext;
            previousTimeScale = Time.timeScale;
            bool opened = false;
            IsPaused = true;
            try
            {
                ui.OpenModal(screenId);
                opened = true;
                if (!IsPaused)
                {
                    return;
                }

                input.SetContext(InputContext.Pause);
                if (IsPaused && controlTimeScale)
                {
                    Time.timeScale = 0f;
                }
            }
            catch (Exception failure)
            {
                try
                {
                    Restore(opened);
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("Pause failed and restoration reported an error.",
                        failure, rollback);
                }

                throw;
            }
        }

        public void Resume()
        {
            EnsureInitialized();
            Restore(true);
        }

        private void OnScreenClosed(string closedId)
        {
            if (closedId == screenId)
            {
                Restore(false);
            }
        }

        private void Restore(bool closeScreen)
        {
            if (!IsPaused || restoring)
            {
                return;
            }

            // Clear before closing: ScreenClosed is synchronous and may call us again.
            IsPaused = false;
            restoring = true;
            var failures = new List<Exception>();
            try
            {
                if (closeScreen && ui != null && ui.IsInitialized && ui.IsOpen(screenId))
                {
                    ui.Close(screenId);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                if (input != null && input.IsInitialized)
                {
                    input.SetContext(previousContext);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                try
                {
                    if (controlTimeScale)
                    {
                        Time.timeScale = previousTimeScale;
                    }
                }
                finally
                {
                    restoring = false;
                }
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            else if (failures.Count > 1)
            {
                throw new AggregateException("Pause restoration reported errors.", failures);
            }
        }

        private void EnsureInitialized()
        {
            if (input == null || ui == null || !input.IsInitialized || !ui.IsInitialized)
            {
                throw new InvalidOperationException("Pause service is not initialized.");
            }
        }
    }
}
