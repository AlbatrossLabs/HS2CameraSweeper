using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace HS2CameraSweep.Capture
{
    /// <summary>
    /// Runs image processing and encoding on worker threads so the sweep keeps moving while files compress.
    /// A few steps (JPG uses Unity's encoder) must run on the main thread; those are queued and pumped from
    /// the plugin's Update. Pending counts both kinds, so Flush waits for everything.
    /// </summary>
    internal static class EncodeQueue
    {
        private static int _pending;
        private static readonly Queue<Action> MainThread = new Queue<Action>();

        public static int Pending => Thread.VolatileRead(ref _pending);

        public static void Run(Action work)
        {
            Interlocked.Increment(ref _pending);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { work(); }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogError("Camera Sweep: saving an image failed: " + ex); }
                finally { Interlocked.Decrement(ref _pending); }
            });
        }

        /// <summary>Queue a step that needs Unity's main thread. Safe to call from any thread.</summary>
        public static void OnMainThread(Action work)
        {
            Interlocked.Increment(ref _pending);
            lock (MainThread) MainThread.Enqueue(work);
        }

        /// <summary>Runs queued main-thread steps. Called every frame.</summary>
        public static void Pump()
        {
            while (true)
            {
                Action next;
                lock (MainThread)
                {
                    if (MainThread.Count == 0) return;
                    next = MainThread.Dequeue();
                }

                try { next(); }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogError("Camera Sweep: saving an image failed: " + ex); }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }

        /// <summary>Coroutine: wait until at most maxPending saves are still in progress.</summary>
        public static IEnumerator WaitUntilBelow(int maxPending)
        {
            while (Pending > maxPending)
            {
                Pump();
                yield return null;
            }
        }
    }
}
