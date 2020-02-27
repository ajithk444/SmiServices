﻿using System;
using Smi.Common.Helpers;


namespace Smi.Common.Tests
{
    /// <summary>
    /// Helper class for working with DateTime's in test methods
    /// </summary>
    public class TestDateTimeProvider : DateTimeProvider
    {
        private readonly DateTime _instance;

        public TestDateTimeProvider()
        {
            _instance = DateTime.UtcNow;
        }

        public override DateTime UtcNow() => _instance;
    }
}
