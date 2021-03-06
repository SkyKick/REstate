﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Resolvers;
using REstate.Schematics;
using StackExchange.Redis;

namespace REstate.Engine.Repositories.Redis
{
    using Metadata = IDictionary<string, string>;

    internal class RedisEngineRepository<TState, TInput>
        : ISchematicRepository<TState, TInput>
        , IMachineRepository<TState, TInput>
    {
        private const string SchematicKeyPrefix = "REstate/Schematics";
        private const string MachinesKeyPrefix = "REstate/Machines";

        private const string MachineSchematicsKeyPrefix = "REstate/MachineSchematics";

        private readonly IDatabase _restateDatabase;

        public RedisEngineRepository(IDatabase restateDatabase)
        {
            _restateDatabase = restateDatabase;
        }

        /// <summary>
        /// Retrieves a previously stored Schematic by name.
        /// </summary>
        /// <param name="schematicName">The name of the Schematic</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="schematicName"/> is null.</exception>
        /// <exception cref="SchematicDoesNotExistException">Thrown when no matching Schematic was found for the given name.</exception>
        public async Task<Schematic<TState, TInput>> RetrieveSchematicAsync(
            string schematicName,
            CancellationToken cancellationToken = default)
        {
            if (schematicName == null) throw new ArgumentNullException(nameof(schematicName));

            var value = await _restateDatabase.StringGetAsync($"{SchematicKeyPrefix}/{schematicName}").ConfigureAwait(false);

            var schematic = LZ4MessagePackSerializer.Deserialize<Schematic<TState, TInput>>(value,
                ContractlessStandardResolver.Instance);

            return schematic;
        }

        /// <summary>
        /// Stores a Schematic, using its <c>SchematicName</c> as the key.
        /// </summary>
        /// <param name="schematic">The Schematic to store</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="schematic"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown if <paramref name="schematic"/> has a null <c>SchematicName</c> property.</exception>
        public async Task<Schematic<TState, TInput>> StoreSchematicAsync(
            Schematic<TState, TInput> schematic,
            CancellationToken cancellationToken = default)
        {
            if (schematic == null) throw new ArgumentNullException(nameof(schematic));
            if (schematic.SchematicName == null) throw new ArgumentException("Schematic must have a name to be stored.", nameof(schematic));

            var schematicBytes = LZ4MessagePackSerializer.Serialize(schematic, ContractlessStandardResolver.Instance);

            await _restateDatabase.StringSetAsync($"{SchematicKeyPrefix}/{schematic.SchematicName}", schematicBytes);

            return schematic;
        }

        /// <summary>
        /// Creates a new Machine from a provided Schematic.
        /// </summary>
        /// <param name="schematicName">The name of the stored Schematic</param>
        /// <param name="machineId">The Id of Machine to create; if null, an Id will be generated.</param>
        /// <param name="metadata">Related metadata for the Machine</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="schematicName"/> is null.</exception>
        public async Task<MachineStatus<TState, TInput>> CreateMachineAsync(
            string schematicName,
            string machineId,
            Metadata metadata,
            CancellationToken cancellationToken = default)
        {
            if (schematicName == null) throw new ArgumentNullException(nameof(schematicName));

            var schematic = await RetrieveSchematicAsync(schematicName, cancellationToken).ConfigureAwait(false);

            return await CreateMachineAsync(schematic, machineId, metadata, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a new Machine from a provided Schematic.
        /// </summary>
        /// <param name="schematic">The Schematic of the Machine</param>
        /// <param name="machineId">The Id of Machine to create; if null, an Id will be generated.</param>
        /// <param name="metadata">Related metadata for the Machine</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="schematic"/> is null.</exception>
        /// <exception cref="SchematicDoesNotExistException">Thrown when no matching Schematic was found for the given name.</exception>
        public async Task<MachineStatus<TState, TInput>> CreateMachineAsync(
            Schematic<TState, TInput> schematic,
            string machineId,
            Metadata metadata,
            CancellationToken cancellationToken = default)
        {
            if (schematic == null) throw new ArgumentNullException(nameof(schematic));

            var id = machineId ?? Guid.NewGuid().ToString();

            var schematicBytes = LZ4MessagePackSerializer.Serialize(schematic, ContractlessStandardResolver.Instance);

            var hash = Convert.ToBase64String(Murmur3.ComputeHashBytes(schematicBytes));

            var schematicResult = await _restateDatabase.StringGetAsync($"{MachineSchematicsKeyPrefix}/{hash}");

            if (!schematicResult.HasValue)
            {
                await _restateDatabase.StringSetAsync($"{MachineSchematicsKeyPrefix}/{hash}", schematicBytes);
            }

            const int commitNumber = 0;
            var updatedTime = DateTimeOffset.UtcNow;

            var record = new RedisMachineStatus<TState, TInput>
            {
                MachineId = id,
                SchematicHash = hash,
                State = schematic.InitialState,
                CommitNumber = commitNumber,
                UpdatedTime = updatedTime,
                Metadata = metadata
            };

            var recordBytes = MessagePackSerializer.Serialize(record);

            await _restateDatabase.StringSetAsync($"{MachinesKeyPrefix}/{machineId}", recordBytes, null, When.NotExists).ConfigureAwait(false);

            return new MachineStatus<TState, TInput>
            {
                MachineId = machineId,
                Schematic = schematic,
                State = schematic.InitialState,
                CommitNumber = commitNumber,
                UpdatedTime = updatedTime,
                Metadata = metadata
            };
        }

        public async Task<ICollection<MachineStatus<TState, TInput>>> BulkCreateMachinesAsync(
            Schematic<TState, TInput> schematic,
            IEnumerable<Metadata> metadata,
            CancellationToken cancellationToken = default)
        {
            var schematicBytes = LZ4MessagePackSerializer.Serialize(schematic, ContractlessStandardResolver.Instance);

            var hash = Convert.ToBase64String(Murmur3.ComputeHashBytes(schematicBytes));

            const int commitNumber = 0;

            var machineRecords =
                metadata.Select(meta =>
                    new MachineStatus<TState, TInput>
                    {
                        MachineId = Guid.NewGuid().ToString(),
                        Schematic = schematic,
                        State = schematic.InitialState,
                        CommitNumber = commitNumber,
                        UpdatedTime = DateTime.UtcNow,
                        Metadata = meta
                    }).ToList();

            await _restateDatabase.StringSetAsync($"{MachineSchematicsKeyPrefix}/{hash}", schematicBytes, null, When.NotExists);

            var batchOps = machineRecords.Select(s =>
                _restateDatabase.StringSetAsync($"{MachinesKeyPrefix}/{s.MachineId}", MessagePackSerializer.Serialize(
                    new RedisMachineStatus<TState, TInput>
                    {
                        MachineId = s.MachineId,
                        SchematicHash = hash,
                        State = s.State,
                        CommitNumber = s.CommitNumber,
                        UpdatedTime = s.UpdatedTime,
                        Metadata = s.Metadata
                    }))
            );

            await Task.WhenAll(batchOps).ConfigureAwait(false);

            return machineRecords;
        }

        public async Task<ICollection<MachineStatus<TState, TInput>>> BulkCreateMachinesAsync(
            string schematicName,
            IEnumerable<Metadata> metadata,
            CancellationToken cancellationToken = default)
        {
            var schematic = await RetrieveSchematicAsync(schematicName, cancellationToken).ConfigureAwait(false);

            
            return await BulkCreateMachinesAsync(schematic, metadata, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes a Machine.
        /// </summary>
        /// <remarks>
        /// Does not throw an exception if a matching Machine was not found.
        /// </remarks>
        /// <param name="machineId">The Id of the Machine to delete</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="machineId"/> is null.</exception>
        public async Task DeleteMachineAsync(
            string machineId,
            CancellationToken cancellationToken = default)
        {
            if (machineId == null) throw new ArgumentNullException(nameof(machineId));

            await _restateDatabase.KeyDeleteAsync($"{MachinesKeyPrefix}/{machineId}").ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves the record for a Machine Status.
        /// </summary>
        /// <param name="machineId">The Id of the Machine</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="machineId"/> is null.</exception>
        /// <exception cref="MachineDoesNotExistException">Thrown when no matching MachineId was found.</exception>
        public async Task<MachineStatus<TState, TInput>> GetMachineStatusAsync(
            string machineId,
            CancellationToken cancellationToken = default)
        {
            if (machineId == null) throw new ArgumentNullException(nameof(machineId));

            var machineBytes = await _restateDatabase.StringGetAsync($"{MachinesKeyPrefix}/{machineId}").ConfigureAwait(false);

            if (!machineBytes.HasValue) throw new MachineDoesNotExistException(machineId);

            var redisRecord = MessagePackSerializer.Deserialize<RedisMachineStatus<TState, TInput>>(
                machineBytes);

            var schematicBytes = await _restateDatabase
                .StringGetAsync($"{MachineSchematicsKeyPrefix}/{redisRecord.SchematicHash}").ConfigureAwait(false);

            var schematic = LZ4MessagePackSerializer.Deserialize<Schematic<TState, TInput>>(
                schematicBytes,
                ContractlessStandardResolver.Instance);

            return new MachineStatus<TState, TInput>
            {
                MachineId = redisRecord.MachineId,
                Schematic = schematic,
                CommitNumber = redisRecord.CommitNumber,
                State = redisRecord.State,
                UpdatedTime = redisRecord.UpdatedTime,
                Metadata = redisRecord.Metadata
            };
        }

        /// <summary>
        /// Updates the Status record of a Machine
        /// </summary>
        /// <param name="machineId">The Id of the Machine</param>
        /// <param name="state">The state to which the Status is set.</param>
        /// <param name="lastCommitNumber">
        /// If provided, will guarentee the update will occur only 
        /// if the value matches the current Status's CommitNumber.
        /// </param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="machineId"/> is null.</exception>
        /// <exception cref="MachineDoesNotExistException">Thrown when no matching MachineId was found.</exception>
        /// <exception cref="StateConflictException">Thrown when a conflict occured on CommitNumber; no update was performed.</exception>
        public async Task<MachineStatus<TState, TInput>> SetMachineStateAsync(
            string machineId,
            TState state,
            long? lastCommitNumber,
            CancellationToken cancellationToken = default)
        {
            if (machineId == null) throw new ArgumentNullException(nameof(machineId));

            var machineKey = $"{MachinesKeyPrefix}/{machineId}";
            var machineBytes = await _restateDatabase.StringGetAsync(machineKey).ConfigureAwait(false);

            var status = MessagePackSerializer.Deserialize<RedisMachineStatus<TState, TInput>>(
                machineBytes);

            if (lastCommitNumber == null || status.CommitNumber == lastCommitNumber)
            {
                status.State = state;
                status.CommitNumber = status.CommitNumber++;
                status.UpdatedTime = DateTimeOffset.UtcNow;
            }
            else
            {
                throw new StateConflictException();
            }

            var newMachineBytes = MessagePackSerializer.Serialize(status);
            
            var transaction = _restateDatabase.CreateTransaction();

            // Veify the record has not changed since read.
            transaction.AddCondition(Condition.StringEqual(machineKey, machineBytes));

            var setTask = transaction.StringSetAsync(machineKey, newMachineBytes);
            var schematicBytesTask = transaction.StringGetAsync($"{MachineSchematicsKeyPrefix}/{status.SchematicHash}");
            var committed = await transaction.ExecuteAsync().ConfigureAwait(false);

            await setTask.ConfigureAwait(false);

            // If the transaction failed, we had a conflict.
            if(!committed) throw new StateConflictException();

            var schematicBytes = await schematicBytesTask.ConfigureAwait(false);

            var schematic = LZ4MessagePackSerializer.Deserialize<Schematic<TState, TInput>>(schematicBytes,
                ContractlessStandardResolver.Instance);

            return new MachineStatus<TState, TInput>
            {
                MachineId = status.MachineId,
                Schematic = schematic,
                State = status.State,
                CommitNumber = status.CommitNumber,
                UpdatedTime = status.UpdatedTime,
                Metadata = status.Metadata
            };
        }
    }
}
