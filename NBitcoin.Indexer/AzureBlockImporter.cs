﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NBitcoin.Indexer
{
	public class ImporterConfiguration
	{
		public static ImporterConfiguration FromConfiguration()
		{
			ImporterConfiguration config = new ImporterConfiguration();
			var account = GetValue("Azure.AccountName", true);
			var key = GetValue("Azure.Key", true);
			config.StorageCredentials = new StorageCredentials(account, key);
			config.Container = GetValue("Azure.Blob.Container", false) ?? "nbitcoinindexer";
			config.BlockDirectory = GetValue("BlockDirectory", true);
			var network = GetValue("Bitcoin.Network", false) ?? "Main";
			config.Network = network.Equals("main", StringComparison.OrdinalIgnoreCase) ?
									Network.Main :
							 network.Equals("test", StringComparison.OrdinalIgnoreCase) ?
							 Network.TestNet : null;
			if(config.Network == null)
				throw new ConfigurationErrorsException("Invalid value " + network + " in appsettings (expecting Main or Test)");
			return config;
		}

		private static string GetValue(string config, bool required)
		{
			var result = ConfigurationManager.AppSettings[config];
			result = String.IsNullOrWhiteSpace(result) ? null : result;
			if(result == null && required)
				throw new ConfigurationErrorsException("AppSetting " + config + " not found");
			return result;
		}
		public ImporterConfiguration()
		{
			ProgressFile = "progress.dat";
			Network = Network.Main;
		}
		public Network Network
		{
			get;
			set;
		}
		public string BlockDirectory
		{
			get;
			set;
		}
		public string Container
		{
			get;
			set;
		}
		public string ProgressFile
		{
			get;
			set;
		}
		public StorageCredentials StorageCredentials
		{
			get;
			set;
		}
		public CloudBlobClient CreateBlobClient()
		{
			return new CloudBlobClient(MakeUri("blob"), StorageCredentials);
		}
		public BlockStore CreateStoreBlock()
		{
			return new BlockStore(BlockDirectory, Network.Main);
		}
		private Uri MakeUri(string clientType)
		{
			return new Uri(String.Format("https://{0}.{1}.core.windows.net/", StorageCredentials.AccountName, clientType), UriKind.Absolute);
		}

		public AzureBlockImporter CreateImporter()
		{
			return new AzureBlockImporter(this);
		}

		public CloudTableClient CreateTableClient()
		{
			return new CloudTableClient(MakeUri("table"), StorageCredentials);
		}
	}

	public class IndexedTransaction : TableEntity
	{
		public IndexedTransaction()
		{

		}

		public IndexedTransaction(uint256 txId)
		{
			Key = CalculateKey(txId);
			RowKey = txId.ToString() + "-m";
		}
		public IndexedTransaction(uint256 txId, uint256 blockId)
		{
			Key = CalculateKey(txId);
			RowKey = txId.ToString() + "-b" + blockId.ToString();
		}

		private static ushort CalculateKey(uint256 txId)
		{
			return (ushort)((txId.GetByte(0) & 0xE0) + (txId.GetByte(1) << 8));
		}


		ushort? _Key;
		[IgnoreProperty]
		public ushort Key
		{
			get
			{
				if(_Key == null)
					_Key = ushort.Parse(PartitionKey);
				return _Key.Value;
			}
			set
			{
				PartitionKey = value.ToString();
				_Key = value;
			}
		}
	}
	public class AzureBlockImporter
	{
		public static AzureBlockImporter CreateBlockImporter(string progressFile = null)
		{
			var config = ImporterConfiguration.FromConfiguration();
			if(progressFile != null)
				config.ProgressFile = progressFile;
			return config.CreateImporter();
		}


		public int TaskCount
		{
			get;
			set;
		}

		private readonly ImporterConfiguration _Configuration;
		public ImporterConfiguration Configuration
		{
			get
			{
				return _Configuration;
			}
		}
		public AzureBlockImporter(ImporterConfiguration configuration)
		{
			if(configuration == null)
				throw new ArgumentNullException("configuration");
			_Configuration = configuration;
		}

		public void StartTransactionImportToAzure()
		{
			SetThrottling();

			BlockingCollection<IndexedTransaction[]> transactions = new BlockingCollection<IndexedTransaction[]>(20);
			CancellationTokenSource stop = new CancellationTokenSource();
			var tasks =
				Enumerable.Range(0, TaskCount).Select(_ => Task.Factory.StartNew(() =>
				{
					try
					{
						foreach(var tx in transactions.GetConsumingEnumerable(stop.Token))
						{
							SendToAzure(tx);
						}
					}
					catch(OperationCanceledException)
					{
					}
				}, TaskCreationOptions.LongRunning)).ToArray();

			var tableClient = Configuration.CreateTableClient();
			var store = Configuration.CreateStoreBlock();
			var saveInterval = TimeSpan.FromMinutes(5);
			Stopwatch watch = new Stopwatch();

			using(IndexerTrace.NewCorrelation("Import transactions to azure started").Open())
			{
				tableClient.GetTableReference("transactions").CreateIfNotExists();
				var startPosition = GetPosition("tx");
				var lastPosition = startPosition;
				IndexerTrace.StartingImportAt(lastPosition);
				var buckets = new MultiDictionary<ushort, IndexedTransaction>();
				int txCount = 0;
				int blockCount = 0;
				watch.Start();
				foreach(var block in store.Enumerate(new DiskBlockPosRange(startPosition)))
				{
					lastPosition = block.BlockPosition;
					foreach(var transaction in block.Item.Transactions)
					{
						var indexed = new IndexedTransaction(transaction.GetHash(), block.Item.Header.GetHash());
						buckets.Add(indexed.Key, indexed);
						var collection = buckets[indexed.Key];
						if(collection.Count == 100)
						{
							PushTransactions(buckets, collection, transactions, ref txCount);
						}
						if(watch.Elapsed > saveInterval)
						{
							watch.Stop();
							watch.Reset();
							foreach(var kv in ((IEnumerable<KeyValuePair<ushort, ICollection<IndexedTransaction>>>)buckets).ToArray())
							{
								PushTransactions(buckets, kv.Value, transactions, ref txCount);
							}
							WaitProcessed(transactions);
							SetPosition(lastPosition, "tx");
							IndexerTrace.PositionSaved(lastPosition);
							watch.Start();
						}
					}
					blockCount++;
					IndexerTrace.BlockCount(blockCount);
				}

				foreach(var kv in ((IEnumerable<KeyValuePair<ushort, ICollection<IndexedTransaction>>>)buckets).ToArray())
				{
					PushTransactions(buckets, kv.Value, transactions, ref txCount);
				}
				WaitProcessed(transactions);
				stop.Cancel();
				Task.WaitAll(tasks);
				SetPosition(lastPosition, "tx");
				IndexerTrace.PositionSaved(lastPosition);
			}
		}

		private void PushTransactions(MultiDictionary<ushort, IndexedTransaction> buckets,
										ICollection<IndexedTransaction> indexedTransactions,
									BlockingCollection<IndexedTransaction[]> transactions,
									ref int txCount)
		{
			var array = indexedTransactions.ToArray();
			txCount += array.Length;
			transactions.Add(array);
			buckets.Remove(array[0].Key);
			IndexerTrace.TxCount(txCount);
		}

		private void SendToAzure(IndexedTransaction[] transactions)
		{

			var client = Configuration.CreateTableClient();
			var table = client.GetTableReference("transactions");

			while(true)
			{
				try
				{
					var batch = new TableBatchOperation();
					foreach(var tx in transactions)
					{
						batch.Add(TableOperation.InsertOrReplace(tx));
					}
					table.ExecuteBatch(batch, new TableRequestOptions()
					{
						PayloadFormat = TablePayloadFormat.JsonNoMetadata,
						MaximumExecutionTime = TimeSpan.FromSeconds(60.0),
						ServerTimeout = TimeSpan.FromSeconds(60.0)
					});
					break;
				}
				catch(Exception ex)
				{
					IndexerTrace.ErrorWhileImportingTxToAzure(ex);
					Thread.Sleep(5000);
				}
			}
		}


		TimeSpan saveInterval = TimeSpan.FromMinutes(5);

		public void StartBlockImportToAzure()
		{
			SetThrottling();
			BlockingCollection<StoredBlock> blocks = new BlockingCollection<StoredBlock>(20);
			CancellationTokenSource stop = new CancellationTokenSource();
			var tasks =
				Enumerable.Range(0, TaskCount).Select(_ => Task.Factory.StartNew(() =>
			{
				try
				{
					foreach(var block in blocks.GetConsumingEnumerable(stop.Token))
					{
						SendToAzure(block);
					}
				}
				catch(OperationCanceledException)
				{
				}
			}, TaskCreationOptions.LongRunning)).ToArray();

			var blobClient = Configuration.CreateBlobClient();
			var store = Configuration.CreateStoreBlock();
			Stopwatch watch = new Stopwatch();
			int blockCount = 0;
			using(IndexerTrace.NewCorrelation("Import blocks to azure started").Open())
			{
				blobClient.GetContainerReference(Configuration.Container).CreateIfNotExists();
				var startPosition = GetPosition();
				var lastPosition = startPosition;
				IndexerTrace.StartingImportAt(lastPosition);

				watch.Start();
				foreach(var block in store.Enumerate(new DiskBlockPosRange(startPosition)))
				{
					lastPosition = block.BlockPosition;
					if(watch.Elapsed > saveInterval)
					{
						watch.Stop();
						watch.Reset();
						WaitProcessed(blocks);
						SetPosition(lastPosition);
						IndexerTrace.PositionSaved(lastPosition);
						watch.Start();
					}
					blocks.Add(block);
					blockCount++;
					IndexerTrace.BlockCount(blockCount);
				}
				WaitProcessed(blocks);
				stop.Cancel();
				Task.WaitAll(tasks);
				SetPosition(lastPosition);
				IndexerTrace.PositionSaved(lastPosition);
			}
		}

		private void WaitProcessed<T>(BlockingCollection<T> collection)
		{
			while(collection.Count != 0)
			{
				Thread.Sleep(1000);
			}
		}

		private void SendToAzure(StoredBlock storedBlock)
		{
			var block = storedBlock.Item;
			var hash = block.GetHash().ToString();
			using(IndexerTrace.NewCorrelation("Upload of " + hash).Open())
			{
				Stopwatch watch = new Stopwatch();
				watch.Start();
				bool failedBefore = false;
				while(true)
				{
					try
					{
						var client = Configuration.CreateBlobClient();
						client.DefaultRequestOptions.SingleBlobUploadThresholdInBytes = 32 * 1024 * 1024;
						var container = client.GetContainerReference(Configuration.Container);
						var blob = container.GetPageBlobReference(hash);
						MemoryStream ms = new MemoryStream();
						block.ReadWrite(ms, true);
						var blockBytes = ms.GetBuffer();

						long length = 512 - (ms.Length % 512);
						if(length == 512)
							length = 0;
						Array.Resize(ref blockBytes, (int)(ms.Length + length));

						try
						{
							blob.UploadFromByteArray(blockBytes, 0, blockBytes.Length, new AccessCondition()
							{
								//Will throw if already exist, save 1 call
								IfNotModifiedSinceTime = failedBefore ? (DateTimeOffset?)null : DateTimeOffset.MinValue
							}, new BlobRequestOptions()
							{
								MaximumExecutionTime = TimeSpan.FromSeconds(60.0),
								ServerTimeout = TimeSpan.FromSeconds(60.0)
							});
							watch.Stop();
							IndexerTrace.BlockUploaded(watch.Elapsed, blockBytes.Length);
							break;
						}
						catch(StorageException ex)
						{
							var alreadyExist = ex.RequestInformation != null && ex.RequestInformation.HttpStatusCode == 412;
							if(!alreadyExist)
								throw;
							watch.Stop();
							IndexerTrace.BlockAlreadyUploaded();
							break;
						}
					}
					catch(Exception ex)
					{
						IndexerTrace.ErrorWhileImportingBlockToAzure(new uint256(hash), ex);
						failedBefore = true;
						Thread.Sleep(5000);
					}
				}
			}
		}

		private static void SetThrottling()
		{
			ServicePointManager.UseNagleAlgorithm = false;
			ServicePointManager.Expect100Continue = false;
			ServicePointManager.DefaultConnectionLimit = 100;
		}




		private void SetPosition(DiskBlockPos diskBlockPos, string name = null)
		{
			name = NormalizeName(name);
			File.WriteAllText(name, diskBlockPos.ToString());
		}

		private DiskBlockPos GetPosition(string name = null)
		{
			name = NormalizeName(name);
			try
			{
				return DiskBlockPos.Parse(File.ReadAllText(name));
			}
			catch
			{
			}
			return new DiskBlockPos(0, 0);
		}

		private string NormalizeName(string name)
		{
			if(name == null)
				name = Configuration.ProgressFile;
			else
			{
				var originalName = Path.GetFileName(name);
				name = name + "-" + originalName;
			}
			return name;
		}


	}
}
