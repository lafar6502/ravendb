﻿using Raven.Tests.Bundles.Replication;

namespace Raven.Tests.Issues
{
	using System;
	using System.IO;
	using System.Threading;

	using Raven.Abstractions.Data;
	using Raven.Abstractions.Extensions;
	using Raven.Client;
	using Raven.Client.Exceptions;
	using Raven.Json.Linq;

	using Xunit;
	using Xunit.Sdk;

	public class RavenDB_689 : ReplicationBase
	{
		public class User
		{
			public long Tick { get; set; }
		}

		/// <summary>
		/// 3 machines
		///   1 -> 3
		///   1 -> 2
		///   2 -> 1
		///- Create users/1 on 1, let it replicate to 2, 3
		///- Disconnect 1 from the network
		///- Update users/1 on 2, let ir replicate 3
		///- Update user/1 on 1 (still disconnected)
		///- Disconnect 2, reconnect 1
		///- Let the conflict happen on 3
		///- Reconnect 2, let the conflict flow to 2 or 1
		///- Resolve the conflict on the conflicted machine
		///- Reconnect 3, should replicate the conflict resolution
		///	**** Right now, it conflict on the conflict, which shouldn't be happening.
		///	**** Show detect that this is successful resolution
		/// </summary>
		[Fact]
		public void TwoMastersOneSlaveAttachmentReplicationIssue()
		{
			var store1 = CreateStore();
			var store2 = CreateStore();
			var store3 = CreateStore();

			SetupReplication(store1.DatabaseCommands, store2.Url, store3.Url);
			SetupReplication(store2.DatabaseCommands, store1.Url, store3.Url);

			store1.DatabaseCommands.PutAttachment("users/1", null, new MemoryStream(new byte[] { 1 }), new RavenJObject());

			WaitForAttachement(store2, "users/1");
			WaitForAttachement(store3, "users/1");

			RemoveReplication(store1.DatabaseCommands);
			RemoveReplication(store2.DatabaseCommands);

			SetupReplication(store2.DatabaseCommands, store3.Url);

			var attachment = store2.DatabaseCommands.GetAttachment("users/1");
			store2.DatabaseCommands.PutAttachment("users/1", attachment.Etag, new MemoryStream(new byte[] { 2 }), attachment.Metadata);

			Thread.Sleep(TimeSpan.FromSeconds(5));

			WaitForAttachment(store1, "users/1", a => Assert.Equal(new byte[] { 1 }, a.Data().ReadData()));
			WaitForAttachment(store3, "users/1", a => Assert.Equal(new byte[] { 2 }, a.Data().ReadData()));

			attachment = store1.DatabaseCommands.GetAttachment("users/1");
			store1.DatabaseCommands.PutAttachment("users/1", attachment.Etag, new MemoryStream(new byte[] { 3 }), attachment.Metadata);

			RemoveReplication(store2.DatabaseCommands);
			SetupReplication(store1.DatabaseCommands, store3.Url);

			var conflictException = Assert.Throws<ConflictException>(() =>
			{
				for (int i = 0; i < RetriesCount; i++)
				{
					store3.DatabaseCommands.GetAttachment("users/1");
					Thread.Sleep(100);
				}
			});

			Assert.Equal("Conflict detected on users/1, conflict must be resolved before the attachment will be accessible", conflictException.Message);

			RemoveReplication(store1.DatabaseCommands);
			RemoveReplication(store2.DatabaseCommands);
			SetupReplication(store1.DatabaseCommands, store2.Url, store3.Url);
			SetupReplication(store2.DatabaseCommands, store1.Url, store3.Url);

			IDocumentStore store;

			try
			{
				conflictException = Assert.Throws<ConflictException>(() =>
				{
					for (int i = 0; i < RetriesCount; i++)
					{
						store1.DatabaseCommands.GetAttachment("users/1");
						Thread.Sleep(100);
					}
				});

				store = store1;
			}
			catch (ThrowsException)
			{
				conflictException = Assert.Throws<ConflictException>(() =>
				{
					for (int i = 0; i < RetriesCount; i++)
					{
						store2.DatabaseCommands.GetAttachment("users/1");
						Thread.Sleep(100);
					}
				});

				store = store2;
			}

			Assert.Equal("Conflict detected on users/1, conflict must be resolved before the attachment will be accessible", conflictException.Message);

			byte[] expectedData = null;

			try
			{
				store.DatabaseCommands.GetAttachment("users/1");
			}
			catch (ConflictException e)
			{
				var c1 = store.DatabaseCommands.GetAttachment(e.ConflictedVersionIds[0]);
				var c2 = store.DatabaseCommands.GetAttachment(e.ConflictedVersionIds[1]);

				expectedData = c1.Data().ReadData();

				store.DatabaseCommands.PutAttachment("users/1", null, new MemoryStream(expectedData), c1.Metadata);
			}

			Thread.Sleep(TimeSpan.FromSeconds(10));

			WaitForAttachment(store1, "users/1", a => Assert.Equal(expectedData, a.Data().ReadData()));
			WaitForAttachment(store2, "users/1", a => Assert.Equal(expectedData, a.Data().ReadData()));
			WaitForAttachment(store3, "users/1", a => Assert.Equal(expectedData, a.Data().ReadData()));
		}

		[Fact]
		public void TwoMastersOneSlaveDocumentReplicationIssue()
		{
			var store1 = CreateStore();
			var store2 = CreateStore();
			var store3 = CreateStore();

			SetupReplication(store1.DatabaseCommands, store2.Url, store3.Url);
			SetupReplication(store2.DatabaseCommands, store1.Url, store3.Url);

			using (var session = store1.OpenSession())
			{
				session.Store(new User { Tick = 1 });
				session.SaveChanges();
			}

			WaitForDocument(store2.DatabaseCommands, "users/1");
			WaitForDocument(store3.DatabaseCommands, "users/1");

			RemoveReplication(store1.DatabaseCommands);
			RemoveReplication(store2.DatabaseCommands);

			SetupReplication(store2.DatabaseCommands, store3.Url);

			using (var session = store2.OpenSession())
			{
				var user = session.Load<User>("users/1");
				user.Tick = 2;
				session.Store(user);
				session.SaveChanges();
			}

			Thread.Sleep(TimeSpan.FromSeconds(5));

			Assert.Equal(1, WaitForDocument<User>(store1, "users/1").Tick);
			Assert.Equal(2, WaitForDocument<User>(store3, "users/1").Tick);

			using (var session = store1.OpenSession())
			{
				var user = session.Load<User>("users/1");
				user.Tick = 3;
				session.Store(user);
				session.SaveChanges();
			}

			RemoveReplication(store2.DatabaseCommands);
			SetupReplication(store1.DatabaseCommands, store3.Url);

			var conflictException = Assert.Throws<ConflictException>(() =>
			{
				for (int i = 0; i < RetriesCount; i++)
				{
					using (var session = store3.OpenSession())
					{
						session.Load<User>("users/1");
						Thread.Sleep(100);
					}
				}
			});

			Assert.Equal("Conflict detected on users/1, conflict must be resolved before the document will be accessible", conflictException.Message);

			RemoveReplication(store1.DatabaseCommands);
			RemoveReplication(store2.DatabaseCommands);
			SetupReplication(store1.DatabaseCommands, store2.Url, store3.Url);
			SetupReplication(store2.DatabaseCommands, store1.Url, store3.Url);

			IDocumentStore store;

			try
			{
				conflictException = Assert.Throws<ConflictException>(
					() =>
					{
						for (int i = 0; i < RetriesCount; i++)
						{
							using (var session = store1.OpenSession())
							{
								session.Load<User>("users/1");
								Thread.Sleep(100);
							}
						}
					});

				store = store1;
			}
			catch (ThrowsException)
			{
				conflictException = Assert.Throws<ConflictException>(
					() =>
					{
						for (int i = 0; i < RetriesCount; i++)
						{
							using (var session = store2.OpenSession())
							{
								session.Load<User>("users/1");
								Thread.Sleep(100);
							}
						}
					});

				store = store2;
			}

			Assert.Equal("Conflict detected on users/1, conflict must be resolved before the document will be accessible", conflictException.Message);

			long expectedTick = -1;

			try
			{
				store.DatabaseCommands.Get("users/1");
			}
			catch (ConflictException e)
			{
				var c1 = store.DatabaseCommands.Get(e.ConflictedVersionIds[0]);
				var c2 = store.DatabaseCommands.Get(e.ConflictedVersionIds[1]);

				store.DatabaseCommands.Put("users/1", null, c1.DataAsJson, c1.Metadata);

				expectedTick = long.Parse(c1.DataAsJson["Tick"].ToString());
			}

			Thread.Sleep(TimeSpan.FromSeconds(5));

			Assert.Equal(expectedTick, WaitForDocument<User>(store1, "users/1").Tick);
			Assert.Equal(expectedTick, WaitForDocument<User>(store2, "users/1").Tick);
			Assert.Equal(expectedTick, WaitForDocument<User>(store3, "users/1").Tick);
		}

		private void WaitForAttachment(IDocumentStore store, string attachmentId, Action<Attachment> assert)
		{
			Attachment attachment = null;
			Exception lastException = null;

			for (var i = 0; i < RetriesCount; i++)
			{
				try
				{
					attachment = WaitForAttachement(store, attachmentId);
					assert(attachment);

					return;
				}
				catch (Exception e)
				{
					lastException = e;
				}

				Thread.Sleep(100);
			}

			if (lastException != null)
			{
				throw lastException;
			}

			throw new Exception("Assert failed from unknown reason.");
		}
	}
}