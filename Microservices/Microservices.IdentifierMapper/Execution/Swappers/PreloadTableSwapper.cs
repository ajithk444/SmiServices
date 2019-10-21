﻿
using Microservices.Common.Options;
using NLog;
using FAnsi.Discovery;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;

namespace Microservices.IdentifierMapper.Execution.Swappers
{
    /// <summary>
    /// Connects to a database containing values to swap identifiers with, and loads it entirely into memory
    /// </summary>
    public class PreloadTableSwapper : ISwapIdentifiers
    {
        private readonly ILogger _logger;

        private IMappingTableOptions _options;

        private Dictionary<string, string> _mapping;
        private readonly object _oDictionaryLock = new object();


        public PreloadTableSwapper()
        {
            _logger = LogManager.GetCurrentClassLogger();
        }

        /// <summary>
        /// Preloads the swap table into memory
        /// </summary>
        /// <param name="options"></param>
        public void Setup(IMappingTableOptions options)
        {
            _logger.Info("Setting up mapping dictionary");

            lock (_oDictionaryLock)
            {
                _options = options;

                DiscoveredTable tbl = options.Discover();

                using (DbConnection con = tbl.Database.Server.GetConnection())
                {
                    con.Open();

                    string sql = string.Format("SELECT {0}, {1} FROM {2}", options.SwapColumnName, options.ReplacementColumnName, tbl.GetFullyQualifiedName());
                    _logger.Debug("SQL: " + sql);

                    DbCommand cmd = tbl.Database.Server.GetCommand(sql, con);
                    cmd.CommandTimeout = _options.TimeoutInSeconds;

                    DbDataReader dataReader = cmd.ExecuteReader();

                    _mapping = new Dictionary<string, string>();

                    _logger.Debug("Populating dictionary from mapping table...");
                    Stopwatch sw = Stopwatch.StartNew();

                    while (dataReader.Read())
                        _mapping.Add(dataReader[_options.SwapColumnName].ToString(), dataReader[_options.ReplacementColumnName].ToString());

                    _logger.Debug("Mapping dictionary populated with " + _mapping.Count + " entries in " + sw.Elapsed.ToString("g"));
                }
            }
        }

        public string GetSubstitutionFor(string toSwap, out string reason)
        {
            lock (_oDictionaryLock)
            {
                if (!_mapping.ContainsKey(toSwap))
                {
                    reason = "PatientID was not in mapping table";
                    return null;
                }

                reason = null;
            }

            return _mapping[toSwap];
        }

        /// <summary>
        /// Clears the cached table and reloads it from the database
        /// </summary>
        public void ClearCache()
        {
            _logger.Debug("Clearing cache and reloading");

            if (_options == null)
                throw new ApplicationException("ClearCache called before mapping options set");

            Setup(_options);
        }
    }
}
