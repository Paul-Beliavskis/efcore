// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    public class SplitQueryingEnumerable<T> : IEnumerable<T>, IAsyncEnumerable<T>, IRelationalQueryingEnumerable
    {
        private readonly RelationalQueryContext _relationalQueryContext;
        private readonly RelationalCommandCache _relationalCommandCache;
        private readonly Func<QueryContext, DbDataReader, ResultContext, SplitQueryResultCoordinator, T> _shaper;
        private readonly Action<QueryContext, IExecutionStrategy, SplitQueryResultCoordinator> _relatedDataLoaders;
        private readonly Func<QueryContext, IExecutionStrategy, SplitQueryResultCoordinator, Task> _relatedDataLoadersAsync;
        private readonly Type _contextType;
        private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _queryLogger;
        private readonly bool _standAloneStateManager;

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public SplitQueryingEnumerable(
            [NotNull] RelationalQueryContext relationalQueryContext,
            [NotNull] RelationalCommandCache relationalCommandCache,
            [NotNull] Func<QueryContext, DbDataReader, ResultContext, SplitQueryResultCoordinator, T> shaper,
            [NotNull] Action<QueryContext, IExecutionStrategy, SplitQueryResultCoordinator> relatedDataLoaders,
            [NotNull] Func<QueryContext, IExecutionStrategy, SplitQueryResultCoordinator, Task> relatedDataLoadersAsync,
            [NotNull] Type contextType,
            bool standAloneStateManager)
        {
            _relationalQueryContext = relationalQueryContext;
            _relationalCommandCache = relationalCommandCache;
            _shaper = shaper;
            _relatedDataLoaders = relatedDataLoaders;
            _relatedDataLoadersAsync = relatedDataLoadersAsync;
            _contextType = contextType;
            _queryLogger = relationalQueryContext.QueryLogger;
            _standAloneStateManager = standAloneStateManager;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _relationalQueryContext.CancellationToken = cancellationToken;

            return new AsyncEnumerator(this);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual IEnumerator<T> GetEnumerator() => new Enumerator(this);

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual DbCommand CreateDbCommand()
            => _relationalCommandCache
                .GetRelationalCommand(_relationalQueryContext.ParameterValues)
                .CreateDbCommand(
                    new RelationalCommandParameterObject(
                        _relationalQueryContext.Connection,
                        _relationalQueryContext.ParameterValues,
                        null,
                        null,
                        null),
                    Guid.Empty,
                    (DbCommandMethod)(-1));

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual string ToQueryString()
            => _relationalQueryContext.RelationalQueryStringFactory.Create(CreateDbCommand());

        private sealed class Enumerator : IEnumerator<T>
        {
            private readonly RelationalQueryContext _relationalQueryContext;
            private readonly RelationalCommandCache _relationalCommandCache;
            private readonly Func<QueryContext, DbDataReader, ResultContext, SplitQueryResultCoordinator, T> _shaper;
            private readonly Action<QueryContext, IExecutionStrategy, SplitQueryResultCoordinator> _relatedDataLoaders;
            private readonly Type _contextType;
            private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _queryLogger;
            private readonly bool _standAloneStateManager;

            private RelationalDataReader _dataReader;
            private SplitQueryResultCoordinator _resultCoordinator;
            private IExecutionStrategy _executionStrategy;

            public Enumerator(SplitQueryingEnumerable<T> queryingEnumerable)
            {
                _relationalQueryContext = queryingEnumerable._relationalQueryContext;
                _relationalCommandCache = queryingEnumerable._relationalCommandCache;
                _shaper = queryingEnumerable._shaper;
                _relatedDataLoaders = queryingEnumerable._relatedDataLoaders;
                _contextType = queryingEnumerable._contextType;
                _queryLogger = queryingEnumerable._queryLogger;
                _standAloneStateManager = queryingEnumerable._standAloneStateManager;
            }

            public T Current { get; private set; }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                try
                {
                    using (_relationalQueryContext.ConcurrencyDetector.EnterCriticalSection())
                    {
                        if (_dataReader == null)
                        {
                            if (_executionStrategy == null)
                            {
                                _executionStrategy = _relationalQueryContext.ExecutionStrategyFactory.Create();
                            }

                            _executionStrategy.Execute(true, InitializeReader, null);
                        }

                        var hasNext = _dataReader.Read();
                        Current = default;

                        if (hasNext)
                        {
                            _resultCoordinator.ResultContext.Values = null;
                            _shaper(_relationalQueryContext, _dataReader.DbDataReader, _resultCoordinator.ResultContext, _resultCoordinator);
                            _relatedDataLoaders?.Invoke(_relationalQueryContext, _executionStrategy, _resultCoordinator);
                            Current = _shaper(
                                _relationalQueryContext, _dataReader.DbDataReader, _resultCoordinator.ResultContext, _resultCoordinator);
                        }

                        return hasNext;
                    }
                }
                catch (Exception exception)
                {
                    _queryLogger.QueryIterationFailed(_contextType, exception);

                    throw;
                }
            }

            private bool InitializeReader(DbContext _, bool result)
            {
                var relationalCommand = _relationalCommandCache.GetRelationalCommand(_relationalQueryContext.ParameterValues);

                _dataReader
                    = relationalCommand.ExecuteReader(
                        new RelationalCommandParameterObject(
                            _relationalQueryContext.Connection,
                            _relationalQueryContext.ParameterValues,
                            _relationalCommandCache.ReaderColumns,
                            _relationalQueryContext.Context,
                            _relationalQueryContext.CommandLogger));

                _resultCoordinator = new SplitQueryResultCoordinator();

                _relationalQueryContext.InitializeStateManager(_standAloneStateManager);

                return result;
            }

            public void Dispose()
            {
                _dataReader?.Dispose();
                _dataReader = null;
            }

            public void Reset() => throw new NotImplementedException();
        }

        private sealed class AsyncEnumerator : IAsyncEnumerator<T>
        {
            private readonly RelationalQueryContext _relationalQueryContext;
            private readonly RelationalCommandCache _relationalCommandCache;
            private readonly Func<QueryContext, DbDataReader, ResultContext, SplitQueryResultCoordinator, T> _shaper;
            private readonly Func<QueryContext, IExecutionStrategy, SplitQueryResultCoordinator, Task> _relatedDataLoaders;
            private readonly Type _contextType;
            private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _queryLogger;
            private readonly bool _standAloneStateManager;

            private RelationalDataReader _dataReader;
            private SplitQueryResultCoordinator _resultCoordinator;
            private IExecutionStrategy _executionStrategy;

            public AsyncEnumerator(SplitQueryingEnumerable<T> queryingEnumerable)
            {
                _relationalQueryContext = queryingEnumerable._relationalQueryContext;
                _relationalCommandCache = queryingEnumerable._relationalCommandCache;
                _shaper = queryingEnumerable._shaper;
                _relatedDataLoaders = queryingEnumerable._relatedDataLoadersAsync;
                _contextType = queryingEnumerable._contextType;
                _queryLogger = queryingEnumerable._queryLogger;
                _standAloneStateManager = queryingEnumerable._standAloneStateManager;
            }

            public T Current { get; private set; }

            public async ValueTask<bool> MoveNextAsync()
            {
                try
                {
                    using (_relationalQueryContext.ConcurrencyDetector.EnterCriticalSection())
                    {
                        if (_dataReader == null)
                        {
                            if (_executionStrategy == null)
                            {
                                _executionStrategy = _relationalQueryContext.ExecutionStrategyFactory.Create();
                            }

                            await _executionStrategy.ExecuteAsync(true, InitializeReaderAsync, null, _relationalQueryContext.CancellationToken).ConfigureAwait(false);
                        }

                        var hasNext = await _dataReader.ReadAsync().ConfigureAwait(false);
                        Current = default;

                        if (hasNext)
                        {
                            _resultCoordinator.ResultContext.Values = null;
                            _shaper(_relationalQueryContext, _dataReader.DbDataReader, _resultCoordinator.ResultContext, _resultCoordinator);
                            if (_relatedDataLoaders != null)
                            {
                                await _relatedDataLoaders(_relationalQueryContext, _executionStrategy, _resultCoordinator).ConfigureAwait(false);
                            }
                            Current = _shaper(
                                _relationalQueryContext, _dataReader.DbDataReader, _resultCoordinator.ResultContext, _resultCoordinator);
                        }

                        return hasNext;
                    }
                }
                catch (Exception exception)
                {
                    _queryLogger.QueryIterationFailed(_contextType, exception);

                    throw;
                }
            }

            private async Task<bool> InitializeReaderAsync(DbContext _, bool result, CancellationToken cancellationToken)
            {
                var relationalCommand = _relationalCommandCache.GetRelationalCommand(_relationalQueryContext.ParameterValues);

                _dataReader
                    = await relationalCommand.ExecuteReaderAsync(
                        new RelationalCommandParameterObject(
                            _relationalQueryContext.Connection,
                            _relationalQueryContext.ParameterValues,
                            _relationalCommandCache.ReaderColumns,
                            _relationalQueryContext.Context,
                            _relationalQueryContext.CommandLogger),
                        cancellationToken)
                    .ConfigureAwait(false);

                _resultCoordinator = new SplitQueryResultCoordinator();

                _relationalQueryContext.InitializeStateManager(_standAloneStateManager);

                return result;
            }

            public ValueTask DisposeAsync()
            {
                if (_dataReader != null)
                {
                    var dataReader = _dataReader;
                    _dataReader = null;

                    return dataReader.DisposeAsync();
                }

                return default;
            }
        }
    }
}
