using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Events;

public interface IEventService
{
    Task<Event> CreateEventAsync(Event evt, int ownerUserId);
    Task<Event?> GetEventAsync(int eventId);
    Task<List<Event>> SearchEventsAsync(string? searchTerm);
    Task<List<EventOccurrence>> GetOccurrencesInRangeAsync(DateTime start, DateTime end);
    Task<EventOccurrence?> GetOccurrenceAsync(int occurrenceId);
    Task<(bool Success, string? Error)> SignUpAsync(int occurrenceId, int userId);
    Task<(bool Success, string? Error)> CancelSignUpAsync(int occurrenceId, int userId);
    Task<(bool Success, string? Error)> CancelOccurrenceAsync(int occurrenceId, int userId);
    Task<(bool Success, string? Error)> UpdateEventAsync(Event evt, int userId);
    Task<(bool Success, string? Error)> TransferOwnershipAsync(int eventId, int currentOwnerId, int newOwnerId);
}
