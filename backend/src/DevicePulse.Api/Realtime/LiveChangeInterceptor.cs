using System.Runtime.CompilerServices;
using DevicePulse.Api.Entities;
using DevicePulse.Api.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DevicePulse.Api.Realtime;

/// <summary>
/// Turns committed alert and device changes into live events.
///
/// Hooked into SaveChanges rather than called from each service, because the changes worth
/// pushing are made in five places — single ingest, bulk ingest, the offline sweeper, the alert
/// endpoints and the device endpoints — and a call sprinkled into each is a call someone forgets
/// to add to the sixth. Here, anything that commits an alert or a status transition is pushed.
///
/// Changes are captured before the save, while the tracker still knows the original values, and
/// published only after it succeeds, so a rolled-back save pushes nothing. Alert ids are read
/// after the save, once the database has assigned them.
///
/// Registered as a singleton; the per-save state is attached to the DbContext instance it
/// belongs to, so concurrent requests cannot see each other's pending changes.
/// </summary>
public sealed class LiveChangeInterceptor : SaveChangesInterceptor
{
    private readonly ILivePublisher _publisher;
    private readonly ConditionalWeakTable<DbContext, PendingChanges> _pending = new();

    public LiveChangeInterceptor(ILivePublisher publisher) => _publisher = publisher;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
            return;

        // Replaced on every attempt rather than appended to, so a save the execution strategy
        // retries is captured once, not once per attempt.
        _pending.AddOrUpdate(context, PendingChanges.From(context.ChangeTracker));
    }

    private void Publish(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending))
            return;

        _pending.Remove(context);

        if (pending.IsEmpty)
            return;

        var (alerts, devices) = pending.ToEvents(DateTime.UtcNow);
        _publisher.Publish(alerts, devices);
    }

    private void Discard(DbContext? context)
    {
        if (context is not null)
            _pending.Remove(context);
    }

    /// <summary>
    /// Entity references plus whatever the tracker knew before the save. The entities are kept
    /// rather than copied, because an added alert's id only exists after the insert.
    /// </summary>
    internal sealed class PendingChanges
    {
        private readonly List<(Alert Alert, AlertChange Change)> _alerts = [];
        private readonly List<Device> _devices = [];

        public bool IsEmpty => _alerts.Count == 0 && _devices.Count == 0;

        public static PendingChanges From(ChangeTracker tracker)
        {
            var pending = new PendingChanges();

            foreach (var entry in tracker.Entries<Alert>())
            {
                var change = entry.State switch
                {
                    EntityState.Added => AlertChange.Raised,
                    EntityState.Modified when Changed(entry.Property(a => a.Status)) => entry.Entity.Status switch
                    {
                        AlertStatus.Acknowledged => AlertChange.Acknowledged,
                        AlertStatus.Resolved => AlertChange.Resolved,
                        _ => (AlertChange?)null
                    },
                    _ => null
                };

                if (change is not null)
                    pending._alerts.Add((entry.Entity, change.Value));
            }

            foreach (var entry in tracker.Entries<Device>())
            {
                var changed = entry.State == EntityState.Added
                    || (entry.State == EntityState.Modified
                        && (Changed(entry.Property(d => d.ConnectivityStatus))
                            || Changed(entry.Property(d => d.LifecycleStatus))));

                if (changed)
                    pending._devices.Add(entry.Entity);
            }

            return pending;
        }

        public (IReadOnlyList<AlertChangedEvent> Alerts, IReadOnlyList<DeviceStatusChangedEvent> Devices) ToEvents(DateTime now)
        {
            var alerts = _alerts
                .Select(a => new AlertChangedEvent(
                    a.Alert.AlertId, a.Alert.DeviceId, a.Change, a.Alert.Severity, a.Alert.Status, a.Alert.Message, now))
                .ToList();

            var devices = _devices
                .Select(d => new DeviceStatusChangedEvent(
                    d.DeviceId, d.DeviceCode, d.DeviceName, d.ConnectivityStatus, d.LifecycleStatus, d.LastSeenAt, now))
                .ToList();

            return (alerts, devices);
        }

        /// <summary>
        /// IsModified alone is not enough: assigning a property the value it already had still
        /// marks it modified under some tracking modes, and that is not a transition.
        /// </summary>
        private static bool Changed<TEntity, TProperty>(PropertyEntry<TEntity, TProperty> property)
            where TEntity : class =>
            property.IsModified && !EqualityComparer<TProperty>.Default.Equals(property.OriginalValue, property.CurrentValue);
    }
}
