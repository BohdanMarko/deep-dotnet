using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

// NOTES:
//
// ExecutionContext - is basically a Dictionary of key/value pairs that is stored in thread local storage.
//                    Actually it is a little bit more fancy than that,
//                    but really that what it is and everything else is kind of an optimization.
//

MyTask.Iterate(PrintAsync(50)).Wait();
static IEnumerable<MyTask> PrintAsync(int count)
{
    for (int i = 0; i < count; i++)
    {
        yield return MyTask.Delay(1000);
        Console.WriteLine(i);
    }
}

for (int i = 0;; i++)
{
    /*await*/ MyTask.Delay(1000);
    Console.WriteLine(i);
}

return 0;

Console.Write("Hello,");
//MyTask.Delay(5000).ContinueWith(() => Console.Write("Bohdan!")).Wait();

MyTask.Delay(3000).ContinueWith(delegate
{
    Console.Write(" World!");
    return MyTask.Delay(2000).ContinueWith(delegate
    {
        Console.Write(" And Bohdan!");
        return MyTask.Delay(2000).ContinueWith(delegate
        {
            Console.Write(" THE END!");
        });
    });
}).Wait();

return 0;
Console.ReadLine();

AsyncLocal<int> myCounter = new AsyncLocal<int>(); // works because of 'execution context'

if (false)
{
    for (int i = 0; i < 1000; i++)
    {
        //int copy = i; // works correctly because copy is in scope of for loop and
        // we create new 'copy' for each iteration, so each queued work item has it's own copy, not shared 'i'
        myCounter.Value = i;
        MyThreadPool.QueueUserWorkItem(delegate
        {
            //Console.WriteLine($"Hello from thread {Environment.CurrentManagedThreadId}");
            //Console.WriteLine(copy);
            Console.WriteLine(myCounter.Value);
            Thread.Sleep(1000);
        });
    }    
}

List<MyTask> tasks = [];
for (int i = 0; i < 100; i++)
{
    myCounter.Value = i;
    tasks.Add(MyTask.Run(delegate
    {
        Console.WriteLine(myCounter.Value);
        Thread.Sleep(1000);
    }));
}

Stopwatch sp =  Stopwatch.StartNew();
MyTask.WhenAll(tasks).Wait();
//foreach (var task in tasks) task.Wait();
sp.Stop();
Console.WriteLine(sp.ElapsedMilliseconds);

Console.ReadLine();

public class MyTask
{
    private bool _isCompleted;
    private Exception? _exception;
    private Action _continuation;
    private ExecutionContext? _context;
    
    public bool IsCompleted
    {
        get
        {
            // just for education purposes, bad practice to write such synchronization in real code
            lock (this)
            {
                return _isCompleted;
            }
        }
    }

    public void SetResult() => Complete(null);

    public void SetException(Exception ex) => Complete(ex);

    private void Complete(Exception? ex)
    {
        lock (this)
        {
            if (_isCompleted is true) throw new InvalidOperationException("Task is already completed!");

            _isCompleted = true;
            _exception = ex;

            if (_continuation is not null)
            {
                MyThreadPool.QueueUserWorkItem(delegate
                {
                    if (_context is null)
                    {
                        _continuation();
                    }
                    else
                    {
                        ExecutionContext.Run(_context, (object? state) => ((Action)state!).Invoke(), _continuation);
                    }
                });
            }
        }
    }

    public void Wait()
    {
        ManualResetEventSlim mres = null;

        lock (this)
        {
            if (_isCompleted is false)
            {
                mres = new ManualResetEventSlim();
                ContinueWith(mres.Set);
            }
        }

        mres?.Wait();

        if (_exception is not null)
        {
            ExceptionDispatchInfo.Throw(_exception);
            //throw new AggregateException(_exception);
        }
    }

    public MyTask ContinueWith(Action continuation)
    {
        MyTask resultTask = new MyTask();

        Action callback = () =>
        {
            try
            {
                continuation();
            }
            catch (Exception e)
            {
                resultTask.SetException(e);
                return;
            }

            resultTask.SetResult();
        };
        
        lock (this)
        {
            if (_isCompleted)
            {
                MyThreadPool.QueueUserWorkItem(callback);
            }
            else
            {
                _continuation = callback;
                _context = ExecutionContext.Capture();
            }
        }
        
        return resultTask;
    }

    public MyTask ContinueWith(Func<MyTask> continuation)
    {
        MyTask resultTask = new MyTask();

        Action callback = () =>
        {
            try
            {
                MyTask next = continuation();
                next.ContinueWith(delegate
                {
                    if (next._exception is null)
                    {
                        resultTask.SetResult();
                    }
                    else
                    {
                        resultTask.SetException(next._exception);
                    }
                });
                //continuation();
            }
            catch (Exception e)
            {
                resultTask.SetException(e);
                return;
            }
        };
        
        lock (this)
        {
            if (_isCompleted)
            {
                MyThreadPool.QueueUserWorkItem(callback);
            }
            else
            {
                _continuation = callback;
                _context = ExecutionContext.Capture();
            }
        }
        
        return resultTask;
    }
    
    public static MyTask Run(Action action)
    {
        MyTask resultTask = new MyTask();

        MyThreadPool.QueueUserWorkItem(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                resultTask.SetException(e);
                return;
            }
            
            resultTask.SetResult();
        });
        
        return resultTask;
    }

    public static MyTask WhenAll(List<MyTask> tasks)
    {
        MyTask resultTask = new MyTask();

        if (tasks.Count == 0)
        {
            resultTask.SetResult();
        }
        else
        {
            int remainingTasks = tasks.Count;
            
            Action continuation = () =>
            {
                if (Interlocked.Decrement(ref remainingTasks) == 0)
                {
                    // TODO: handle exceptions
                    resultTask.SetResult();
                }
            };
            
            tasks.ForEach(t => t.ContinueWith(continuation));
        }

        return resultTask;
    }

    public static MyTask Delay(int timeout)
    {
        MyTask resultTask = new MyTask();
        new Timer(_ => resultTask.SetResult()).Change(timeout, -1);
        return resultTask;
    }

    public static MyTask Iterate(IEnumerable<MyTask> tasks)
    {
        MyTask resultTask = new MyTask();

        IEnumerator<MyTask> enumerator = tasks.GetEnumerator();
        void MoveNext()
        {
            try
            {
                if (enumerator.MoveNext())
                {
                    MyTask next = enumerator.Current;
                    next.ContinueWith(MoveNext);
                    return;
                }
            }
            catch (Exception e)
            {
                resultTask.SetException(e);
                return;
            }
            
            resultTask.SetResult();
        }
        
        MoveNext();

        return resultTask;
    }
}

public static class MyThreadPool
{
    static readonly BlockingCollection<(Action, ExecutionContext?)> s_workItems 
        = new BlockingCollection<(Action, ExecutionContext?)>();
    
    public static void QueueUserWorkItem(Action action) => s_workItems.Add((action, ExecutionContext.Capture()));

    static MyThreadPool()
    {
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            new Thread(() =>
                {
                    while (true)
                    {
                        (Action workItem, ExecutionContext? context) = s_workItems.Take();
                        if (context is null)
                        {
                            workItem();   
                        }
                        else
                        {
                            // Just another view how it can be written
                            // Uses workItem through closure
                            //ExecutionContext.Run(context, delegate { workItem(); }, null);
                            
                            // 'object? state' argument is our workItem
                            ExecutionContext.Run(context, (object? state) => ((Action)state!).Invoke(), workItem);
                            
                            // works fine as well:
                            //ExecutionContext.Restore(context);
                            //workItem();
                        }
                    }
                })
            {
                IsBackground = true
            }.Start();
        }
    }
}