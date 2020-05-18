﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Dicom;
using NLog;
using NUnit.Framework;

namespace Smi.Common.Tests
{
    /// <summary>
    /// helper for asynchronous tests, awaits for certain conditions to be true within a given timeout (or infinite timeout if debugger is attached)
    /// </summary>
    public class TestTimelineAwaiter
    {
        /// <summary>
        /// Blocks until <paramref name="condition"/> is met or the <paramref name="timeout"/> is reached.  Polls <paramref name="throwIfAnyFunc"/>
        /// (if provided) to check for Exceptions (which will break the wait).
        /// Can be made more efficient by providing an object lockobj
        /// which will be Monitor.Pulse()d on state changes, as in Consumer.
        ///
        /// <para>During debugging <paramref name="timeout"/> is ignored </para>
        /// </summary>
        /// <param name="condition"></param>
        /// <param name="timeoutMessage"></param>
        /// <param name="timeout"></param>
        /// <param name="throwIfAnyFunc"></param>
        /// <param name="lockobj"</param>
        public void Await(Func<bool> condition,string timeoutMessage= null,int timeout = 30000, Func<IEnumerable<Exception>> throwIfAnyFunc = null, object lockobj=null)
        {
            if (Debugger.IsAttached)
                timeout = int.MaxValue;

            
            while (!condition() && timeout > 0)
            {
                if (lockobj is null)
                {
                    timeout -= 100;
                    Thread.Sleep(100);
                }
                else
                {
                    lock (lockobj)
                        if (!Monitor.Wait(lockobj, 1000))
                            timeout -= 1000;
                }

                var exceptions = throwIfAnyFunc?.Invoke()?.ToArray();

                if (exceptions != null && exceptions.Any())
                {
                    var logger = LogManager.GetCurrentClassLogger();

                    foreach (Exception ex in exceptions)
                        logger.Error(ex);

                    LogManager.Flush();
                    
                    throw exceptions.Length == 1
                        ? exceptions.Single()
                        : new AggregateException(exceptions);
                }
                    
            }

            if (timeout <= 0)
                Assert.Fail(timeoutMessage ?? "Failed to reach the condition after the expected timeout");
        }
    }
}
