public record ServerRequestLog(
    DateTime Timestamp,
    string Method,
    string Path,
    int StatusCode
);