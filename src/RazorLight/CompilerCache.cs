﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace RazorLight
{
	/// <summary>
	/// Caches the result of runtime compilation of Razor files for the duration of the application lifetime.
	/// </summary>
	public class CompilerCache : IDisposable
	{
		private readonly IFileProvider _fileProvider;
		private readonly IMemoryCache _cache;
		private readonly object _cacheLock = new object();

		private readonly ConcurrentDictionary<string, string> _normalizedPathLookup =
			new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

		/// <summary>
		/// Initializes a new instance of <see cref="CompilerCache"/>.
		/// </summary>
		/// <param name="fileProvider"><see cref="IFileProvider"/> used to locate Razor views.</param>
		public CompilerCache(IFileProvider fileProvider)
		{
			if (fileProvider == null)
			{
				throw new ArgumentNullException(nameof(fileProvider));
			}

			_fileProvider = fileProvider;
			_cache = new MemoryCache(new MemoryCacheOptions { CompactOnMemoryPressure = false });
		}

		/// <inheritdoc />
		public string GetOrAdd(
			string relativePath,
			Func<string, string> compile)
		{
			if (relativePath == null)
			{
				throw new ArgumentNullException(nameof(relativePath));
			}

			if (compile == null)
			{
				throw new ArgumentNullException(nameof(compile));
			}

			Task<string> cacheEntry;
			// Attempt to lookup the cache entry using the passed in path. This will succeed if the path is already
			// normalized and a cache entry exists.
			if (!_cache.TryGetValue(relativePath, out cacheEntry))
			{
				string normalizedPath = GetNormalizedPath(relativePath);
				if (!_cache.TryGetValue(normalizedPath, out cacheEntry))
				{
					cacheEntry = CreateCacheEntry(relativePath, normalizedPath, compile);
				}
			}

			// The Task does not represent async work and is meant to provide per-entry locking.
			// Hence it is ok to perform .GetResult() to read the result.
			return cacheEntry.GetAwaiter().GetResult();
		}

		private Task<string> CreateCacheEntry(
			string relativePath,
			string normalizedPath,
			Func<string, string> compile)
		{
			TaskCompletionSource<string> compilationTaskSource = null;
			MemoryCacheEntryOptions cacheEntryOptions = null;
			IFileInfo fileInfo = null;
			Task<string> cacheEntry;

			// Safe races cannot be allowed when compiling Razor pages. To ensure only one compilation request succeeds
			// per file, we'll lock the creation of a cache entry. Creating the cache entry should be very quick. The
			// actual work for compiling files happens outside the critical section.
			lock (_cacheLock)
			{
				if (_cache.TryGetValue(normalizedPath, out cacheEntry))
				{
					return cacheEntry;
				}

				fileInfo = _fileProvider.GetFileInfo(normalizedPath);
				if (!fileInfo.Exists)
				{
					IChangeToken expirationToken = _fileProvider.Watch(normalizedPath);
					cacheEntry = Task.FromResult("");

					cacheEntryOptions = new MemoryCacheEntryOptions();
					cacheEntryOptions.AddExpirationToken(expirationToken);
				}
				else
				{
					cacheEntryOptions = GetMemoryCacheEntryOptions(normalizedPath);

					// A file exists and needs to be compiled.
					compilationTaskSource = new TaskCompletionSource<string>();
					cacheEntry = compilationTaskSource.Task;
				}

				cacheEntry = _cache.Set<Task<string>>(normalizedPath, cacheEntry, cacheEntryOptions);
			}

			if (compilationTaskSource != null)
			{
				// Indicates that the file was found and needs to be compiled.
				Debug.Assert(fileInfo != null && fileInfo.Exists);
				Debug.Assert(cacheEntryOptions != null);

				try
				{
					string compilationResult = compile(relativePath);
					compilationTaskSource.SetResult(compilationResult);
				}
				catch (Exception ex)
				{
					compilationTaskSource.SetException(ex);
				}
			}

			return cacheEntry;
		}

		private MemoryCacheEntryOptions GetMemoryCacheEntryOptions(string relativePath)
		{
			var options = new MemoryCacheEntryOptions();
			options.AddExpirationToken(_fileProvider.Watch(relativePath));

			return options;
		}

		private string GetNormalizedPath(string relativePath)
		{
			Debug.Assert(relativePath != null);
			if (relativePath.Length == 0)
			{
				return relativePath;
			}

			string normalizedPath;
			if (!_normalizedPathLookup.TryGetValue(relativePath, out normalizedPath))
			{
				var builder = new StringBuilder(relativePath);
				builder.Replace('\\', '/');
				if (builder[0] != '/')
				{
					builder.Insert(0, '/');
				}
				normalizedPath = builder.ToString();
				_normalizedPathLookup.TryAdd(relativePath, normalizedPath);
			}

			return normalizedPath;
		}

		public void Dispose()
		{
			if(_cache != null)
			{
				_cache.Dispose();
			}
		}
	}
}