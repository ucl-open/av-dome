using Bonsai;
using Bonsai.Shaders;
using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

// Bonsai.Shaders' RenderTexture (inside BonVision's MeshMapping and GammaCorrection) deletes its
// framebuffer with GL calls in a Finally, on whichever thread disposes the workflow. The controller's
// Stop (and any error raised by OpenRigs.Control) ends the workflow on the ControlServer's own thread,
// which has no GL context, so the GL call raised an access violation that killed Bonsai before the
// experiment's CSV writers and manifest were flushed.
[Combinator]
[Description("Disposes the sequence it is given (a group holding the shader window and BonVision's render targets) on the shader window's render thread, where the GL context is current, and waits for it. Its elements are not passed on. If the window is not running, or the render thread does not answer within TimeoutSeconds, it disposes on the calling thread instead.")]
[WorkflowElementCategory(ElementCategory.Sink)]
public class DisposeOnRenderThread
{
    public DisposeOnRenderThread()
    {
        TimeoutSeconds = 2;
    }

    [Description("How long to wait for the render thread to dispose the sequence before disposing it on the calling thread.")]
    public double TimeoutSeconds { get; set; }

    public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
    {
        return Observable.Create<TSource>(observer =>
        {
            var gate = new object();
            ShaderWindow window = null;
            Thread renderThread = null;

            // The render thread is learnt from an update the window runs itself, so a window replayed
            // to this subscription on another thread is not mistaken for it.
            var windowSubscription = ShaderManager.WindowSource.Subscribe(
                w =>
                {
                    lock (gate) { window = w; }
                    w.Update(() =>
                    {
                        lock (gate) { renderThread = Thread.CurrentThread; }
                    });
                },
                error => { },
                () => { });

            var inner = new SingleAssignmentDisposable();
            inner.Disposable = source.IgnoreElements().Subscribe(observer);
            var timeout = TimeSpan.FromSeconds(TimeoutSeconds);

            return Disposable.Create(() =>
            {
                try
                {
                    ShaderWindow w;
                    Thread t;
                    lock (gate)
                    {
                        w = window;
                        t = renderThread;
                    }

                    if (w == null || t == null || t == Thread.CurrentThread || !t.IsAlive || w.IsExiting || !w.Exists)
                    {
                        inner.Dispose();
                        return;
                    }

                    Exception error = null;
                    using (var done = new ManualResetEventSlim(false))
                    {
                        w.Update(() =>
                        {
                            try { inner.Dispose(); }
                            catch (Exception ex) { error = ex; }
                            finally
                            {
                                try { done.Set(); }
                                catch (ObjectDisposedException) { }
                            }
                        });

                        if (!done.Wait(timeout))
                        {
                            Console.Error.WriteLine(
                                "DisposeOnRenderThread: the render thread did not dispose within {0} s; disposing on thread {1} instead.",
                                TimeoutSeconds, Thread.CurrentThread.ManagedThreadId);
                            inner.Dispose();
                        }
                    }

                    if (error != null)
                    {
                        ExceptionDispatchInfo.Capture(error).Throw();
                    }
                }
                finally
                {
                    windowSubscription.Dispose();
                }
            });
        });
    }
}
