namespace Orders.Api.Services;

public class NotFoundException(string message) : Exception(message);
public class ConflictException(string message) : Exception(message);
public class BusinessRuleException(string message) : Exception(message);
public class AuthenticationFailedException(string message) : Exception(message);
public class ServiceUnavailableException(string message) : Exception(message);