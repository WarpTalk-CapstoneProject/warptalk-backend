namespace WarpTalk.BillingService.Domain.Exceptions;

public sealed class ConcurrencyConflictException(
    string message,
    Exception innerException) : Exception(message, innerException);
