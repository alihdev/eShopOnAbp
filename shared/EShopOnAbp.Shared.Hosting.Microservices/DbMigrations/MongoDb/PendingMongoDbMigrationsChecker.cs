﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Serilog;
using Volo.Abp.Data;
using Volo.Abp.DistributedLocking;
using Volo.Abp.MongoDB;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace EShopOnAbp.Shared.Hosting.Microservices.DbMigrations.MongoDb;

public class PendingMongoDbMigrationsChecker<TDbContext> : PendingMigrationsCheckerBase
    where TDbContext : AbpMongoDbContext
{
    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected IServiceProvider ServiceProvider { get; }
    protected ICurrentTenant CurrentTenant { get; }
    protected IDataSeeder DataSeeder { get; }
    protected IAbpDistributedLock DistributedLockProvider { get; }
    protected string DatabaseName { get; }

    protected PendingMongoDbMigrationsChecker(
        IUnitOfWorkManager unitOfWorkManager,
        IServiceProvider serviceProvider,
        ICurrentTenant currentTenant,
        IDataSeeder dataSeeder,
        IAbpDistributedLock distributedLockProvider,
        string databaseName)
    {
        UnitOfWorkManager = unitOfWorkManager;
        ServiceProvider = serviceProvider;
        CurrentTenant = currentTenant;
        DataSeeder = dataSeeder;
        DistributedLockProvider = distributedLockProvider;
        DatabaseName = databaseName;
    }

    public virtual async Task CheckAndApplyDatabaseMigrationsAsync()
    {
        await TryAsync(async () =>
        {
            using (CurrentTenant.Change(null))
            {
                // Create database tables if needed
                using (var uow = UnitOfWorkManager.Begin(requiresNew: true, isTransactional: false))
                {
                    await MigrateDatabaseSchemaAsync();

                    await DataSeeder.SeedAsync();

                    await uow.CompleteAsync();
                }
            }
        });
    }

    /// <summary>
    /// Apply scheme update for MongoDB Database.
    /// </summary>
    protected virtual async Task MigrateDatabaseSchemaAsync()
    {
        await using (var handle = await DistributedLockProvider.TryAcquireAsync($"Migration_Mongo_{DatabaseName}"))
        {
            if (handle == null)
            {
                return;
            }

            Log.Information($"Lock is acquired for db migration and seeding on database named: {DatabaseName}...");

            using (var uow = UnitOfWorkManager.Begin(requiresNew: true, isTransactional: false))
            {
                var dbContexts = ServiceProvider.GetServices<IAbpMongoDbContext>();
                var connectionStringResolver = ServiceProvider.GetRequiredService<IConnectionStringResolver>();

                foreach (var dbContext in dbContexts)
                {
                    var connectionString =
                        await connectionStringResolver.ResolveAsync(
                            ConnectionStringNameAttribute.GetConnStringName(dbContext.GetType()));
                    if (connectionString.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    var mongoUrl = new MongoUrl(connectionString);
                    var databaseName = mongoUrl.DatabaseName;
                    var client = new MongoClient(mongoUrl);

                    if (databaseName.IsNullOrWhiteSpace())
                    {
                        databaseName = ConnectionStringNameAttribute.GetConnStringName(dbContext.GetType());
                    }

                    (dbContext as AbpMongoDbContext)?.InitializeCollections(client.GetDatabase(databaseName));
                }

                await uow.CompleteAsync();
            }
            Log.Information($"Lock is released for: {DatabaseName}...");
        }
    }
}