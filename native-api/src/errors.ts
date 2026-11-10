// Typed API errors. `code` values are part of the /v1 contract (SPEC §12.6)
// and are safe to display; `details` must never carry secrets.

export type ErrorCode =
  | 'NOT_CONFIGURED'
  | 'AUTH_REQUIRED'
  | 'ITEM_LOGIN_REQUIRED'
  | 'NETWORK_UNAVAILABLE'
  | 'RATE_LIMITED'
  | 'SYNC_IN_PROGRESS'
  | 'DATA_CORRUPT'
  | 'VALIDATION_ERROR'
  | 'NOT_FOUND'
  | 'CONFLICT'
  | 'UNAUTHORIZED'
  | 'INTERNAL_ERROR';

const STATUS: Record<ErrorCode, number> = {
  NOT_CONFIGURED: 409,
  AUTH_REQUIRED: 401,
  ITEM_LOGIN_REQUIRED: 409,
  NETWORK_UNAVAILABLE: 503,
  RATE_LIMITED: 429,
  SYNC_IN_PROGRESS: 409,
  DATA_CORRUPT: 500,
  VALIDATION_ERROR: 400,
  NOT_FOUND: 404,
  CONFLICT: 409,
  UNAUTHORIZED: 401,
  INTERNAL_ERROR: 500,
};

export class ApiError extends Error {
  readonly code: ErrorCode;
  readonly retryable: boolean;
  readonly details: Record<string, string> | null;

  constructor(
    code: ErrorCode,
    message: string,
    opts: { retryable?: boolean; details?: Record<string, string> } = {},
  ) {
    super(message);
    this.code = code;
    this.retryable = opts.retryable ?? false;
    this.details = opts.details ?? null;
  }

  get status(): number {
    return STATUS[this.code];
  }
}

export const notFound = (what: string) => new ApiError('NOT_FOUND', `${what} not found.`);
export const validation = (message: string, details?: Record<string, string>) =>
  new ApiError('VALIDATION_ERROR', message, { details });
