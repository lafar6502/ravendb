﻿using System;
using System.Runtime.Caching;
using System.Threading;
using Raven.Abstractions.Extensions;
using Raven.Json.Linq;

namespace Raven.Database.Impl
{
    public class DocumentCacher : IDocumentCacher
    {
        private readonly MemoryCache cachedSerializedDocuments = new MemoryCache(typeof(DocumentCacher).FullName + ".Cache");

		[ThreadStatic]
    	private static bool skipSettingDocumentInCache;

		public static IDisposable SkipSettingDocumentsInDocumentCache()
		{
			var old = skipSettingDocumentInCache;
			skipSettingDocumentInCache = true;

			return new DisposableAction(() => skipSettingDocumentInCache = old);
		}

        public CachedDocument GetCachedDocument(string key, Guid etag)
        {
            var cachedDocument = (CachedDocument)cachedSerializedDocuments.Get("Doc/" + key + "/" + etag);
            if (cachedDocument == null)
                return null;
            return new CachedDocument
            {
                Document = cachedDocument.Document.CreateSnapshot(),
                Metadata = cachedDocument.Metadata.CreateSnapshot()
            };
        }

        public void SetCachedDocument(string key, Guid etag, RavenJObject doc, RavenJObject metadata)
        {
			if (skipSettingDocumentInCache)
				return;

        	var documentClone = ((RavenJObject)doc.CloneToken());
			documentClone.EnsureSnapshot();
        	var metadataClone = ((RavenJObject)metadata.CloneToken());
			metadataClone.EnsureSnapshot();
        	cachedSerializedDocuments["Doc/" + key + "/" + etag] = new CachedDocument
            {
                Document = documentClone,
                Metadata = metadataClone
            };
        }

    	public void RemoveCachedDocument(string key, Guid etag)
    	{
    		cachedSerializedDocuments.Remove("Doc/" + key + "/" + etag);
    	}

    	public void Dispose()
        {
            cachedSerializedDocuments.Dispose();
        }
    }
}