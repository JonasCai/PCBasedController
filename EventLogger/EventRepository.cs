using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PCBasedController.EventLogger
{
    public class EventRepository : IEventRepository, IDisposable
    {
        private readonly IMongoCollection<EventBase> _collection;
        private readonly ILogger<EventRepository> _logger;
        private readonly Channel<EventBase> _bufferChannel;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _processTask;

        public EventRepository(IOptions<MongoDbSettings> dbOptions, ILogger<EventRepository> logger)
        {
            _logger = logger;
            var settings = dbOptions.Value;

            // 初始化 MongoDB 连接
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _collection = database.GetCollection<EventBase>(settings.CollectionName);

            // 配置 1万 容量的防阻塞通道
            var options = new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            };
            _bufferChannel = Channel.CreateBounded<EventBase>(options);

            // 启动后台存盘线程
            _processTask = Task.Run(() => ProcessEventsAsync(_cts.Token));
        }

        public void Save(EventBase evt)
        {
            if (!_bufferChannel.Writer.TryWrite(evt))
                _logger.LogWarning($"事件缓冲池写入失败:{evt.EventId} | {evt.Severity}");
        }

        private async Task ProcessEventsAsync(CancellationToken cancellationToken)
        {
            var batch = new List<EventBase>();

            try
            {
                await foreach (var evt in _bufferChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    batch.Add(evt);

                    // 凑满 10 条或者通道暂空时，执行一次数据库 IO
                    if (batch.Count >= 10 || _bufferChannel.Reader.Count == 0)
                    {
                        await WriteBatchToDbAsync(batch, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常停机 */ }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "事件存盘后台任务发生致命异常！");
            }
        }

        private async Task WriteBatchToDbAsync(List<EventBase> batch, CancellationToken cancellationToken)
        {
            while (batch.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var bulkOps = new List<WriteModel<EventBase>>();

                    // 用于在当前批次内追踪刚触发的报警
                    var pendingInserts = new Dictionary<Guid, AlarmModel>();

                    foreach (var evt in batch)
                    {
                        if (evt is AlarmModel alarm)
                        {
                            if (alarm.State == AlarmState.Arrived)
                            {
                                // 到达事件：加入追踪字典，并准备 Insert
                                pendingInserts[alarm.InstanceId] = alarm;
                            }
                            else if (alarm.State == AlarmState.Left)
                            {
                                // 离开事件：检查同批次中是否有它的“到达”事件
                                if (pendingInserts.TryGetValue(alarm.InstanceId, out var arrivedAlarm))
                                {
                                    pendingInserts[alarm.InstanceId] = arrivedAlarm with
                                    {
                                        TimeCleared = alarm.TimeCleared,
                                        State = AlarmState.Left
                                    };
                                }
                                else
                                {
                                    // 3. 只有当到达事件在以前的批次早就入库了，才老老实实去数据库 Update
                                    var filter = Builders<EventBase>.Filter.And(
                                        Builders<EventBase>.Filter.OfType<AlarmModel>(a =>
                                            a.InstanceId == alarm.InstanceId)
                                    );

                                    var update = Builders<EventBase>.Update
                                        .Set(nameof(AlarmModel.State), AlarmState.Left)
                                        .Set(nameof(AlarmModel.TimeCleared), alarm.TimeCleared);

                                    bulkOps.Add(new UpdateOneModel<EventBase>(filter, update));
                                }
                            }
                        }
                        else
                        {
                            // 普通 MessageModel，直接 Insert
                            bulkOps.Add(new InsertOneModel<EventBase>(evt));
                        }
                    }

                    foreach (var pendingInsert in pendingInserts.Values)
                        bulkOps.Add(new InsertOneModel<EventBase>(pendingInsert));

                    if (bulkOps.Any())
                        await _collection.BulkWriteAsync(bulkOps, new BulkWriteOptions { IsOrdered = false }, cancellationToken);

                    batch.Clear();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MongoDB 写入失败。5秒后重试...");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        public void Dispose()
        {
            _bufferChannel.Writer.Complete();
            _cts.CancelAfter(TimeSpan.FromSeconds(3)); // 给3秒收尾时间
            try { _processTask.GetAwaiter().GetResult(); } catch { }
            _cts.Dispose();
        }
    }

    public interface IEventRepository
    {
        void Save(EventBase evt);
    }

    public class MongoDbSettings
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string CollectionName { get; set; } = string.Empty;
    }
}
