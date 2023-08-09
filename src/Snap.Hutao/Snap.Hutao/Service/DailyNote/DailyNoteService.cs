﻿// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Snap.Hutao.Core.Database;
using Snap.Hutao.Core.DependencyInjection.Abstraction;
using Snap.Hutao.Message;
using Snap.Hutao.Model.Entity;
using Snap.Hutao.Model.Entity.Database;
using Snap.Hutao.Service.Notification;
using Snap.Hutao.Service.User;
using Snap.Hutao.ViewModel.User;
using Snap.Hutao.Web.Hoyolab;
using Snap.Hutao.Web.Hoyolab.Takumi.GameRecord;
using System.Collections.ObjectModel;
using WebDailyNote = Snap.Hutao.Web.Hoyolab.Takumi.GameRecord.DailyNote.DailyNote;

namespace Snap.Hutao.Service.DailyNote;

/// <summary>
/// 实时便笺服务
/// </summary>
[HighQuality]
[ConstructorGenerated]
[Injection(InjectAs.Singleton, typeof(IDailyNoteService))]
internal sealed partial class DailyNoteService : IDailyNoteService, IRecipient<UserRemovedMessage>
{
    private readonly IServiceProvider serviceProvider;
    private readonly IDailyNoteDbService dailyNoteDbService;
    private readonly IUserService userService;
    private readonly ITaskContext taskContext;

    private ObservableCollection<DailyNoteEntry>? entries;

    /// <inheritdoc/>
    public void Receive(UserRemovedMessage message)
    {
        // Database items have been deleted by cascade deleting.
        taskContext.InvokeOnMainThread(() => entries?.RemoveWhere(n => n.UserId == message.RemovedUserId));
    }

    /// <inheritdoc/>
    public async ValueTask AddDailyNoteAsync(UserAndUid role)
    {
        string roleUid = role.Uid.Value;

        if (!dailyNoteDbService.ContainsUid(roleUid))
        {
            DailyNoteEntry newEntry = DailyNoteEntry.From(role);

            Web.Response.Response<WebDailyNote> dailyNoteResponse = await serviceProvider
                .GetRequiredService<IOverseaSupportFactory<IGameRecordClient>>()
                .Create(PlayerUid.IsOversea(roleUid))
                .GetDailyNoteAsync(role)
                .ConfigureAwait(false);

            if (dailyNoteResponse.IsOk())
            {
                newEntry.UpdateDailyNote(dailyNoteResponse.Data);
            }

            newEntry.UserGameRole = userService.GetUserGameRoleByUid(roleUid);
            await dailyNoteDbService.AddDailyNoteEntryAsync(newEntry).ConfigureAwait(false);

            await taskContext.SwitchToMainThreadAsync();
            entries?.Add(newEntry);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ObservableCollection<DailyNoteEntry>> GetDailyNoteEntryCollectionAsync()
    {
        if (entries is null)
        {
            // IUserService.GetUserGameRoleByUid only usable after call IUserService.GetRoleCollectionAsync
            await userService.GetRoleCollectionAsync().ConfigureAwait(false);
            await RefreshDailyNotesAsync().ConfigureAwait(false);

            List<DailyNoteEntry> entryList = dailyNoteDbService.GetDailyNoteEntryList();
            entryList.ForEach(entry => { entry.UserGameRole = userService.GetUserGameRoleByUid(entry.Uid); });
            entries = new(entryList);
        }

        return entries;
    }

    /// <inheritdoc/>
    public async ValueTask RefreshDailyNotesAsync()
    {
        foreach (DailyNoteEntry entry in dailyNoteDbService.GetDailyNoteEntryIncludeUserList())
        {
            Web.Response.Response<WebDailyNote> dailyNoteResponse = await serviceProvider
                .GetRequiredService<IOverseaSupportFactory<IGameRecordClient>>()
                .Create(PlayerUid.IsOversea(entry.Uid))
                .GetDailyNoteAsync(new(entry.User, entry.Uid))
                .ConfigureAwait(false);

            if (dailyNoteResponse.IsOk())
            {
                WebDailyNote dailyNote = dailyNoteResponse.Data!;

                // cache
                await taskContext.SwitchToMainThreadAsync();
                entries?.SingleOrDefault(e => e.UserId == entry.UserId && e.Uid == entry.Uid)?.UpdateDailyNote(dailyNote);

                // database
                await new DailyNoteNotificationOperation(serviceProvider, entry).SendAsync().ConfigureAwait(false);
                entry.DailyNote = dailyNote;
                await dailyNoteDbService.UpdateDailyNoteEntryAsync(entry).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask RemoveDailyNoteAsync(DailyNoteEntry entry)
    {
        await taskContext.SwitchToMainThreadAsync();
        ArgumentNullException.ThrowIfNull(entries);
        entries.Remove(entry);

        await taskContext.SwitchToBackgroundAsync();
        await dailyNoteDbService.DeleteDailyNoteEntryByIdAsync(entry.InnerId).ConfigureAwait(false);
    }

    public async ValueTask UpdateDailyNoteAsync(DailyNoteEntry entry)
    {
        await taskContext.SwitchToBackgroundAsync();
        await dailyNoteDbService.UpdateDailyNoteEntryAsync(entry).ConfigureAwait(false);
    }
}