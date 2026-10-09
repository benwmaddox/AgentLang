#define WIN32_LEAN_AND_MEAN
#define PSAPI_VERSION 2
#include <windows.h>
#include <psapi.h>
#include <errno.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#if defined(_WIN64)
_Static_assert(sizeof(JOBOBJECT_BASIC_LIMIT_INFORMATION) == 64,
               "unexpected 64-bit job limit layout");
_Static_assert(sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION) == 144,
               "unexpected 64-bit extended job limit layout");
_Static_assert(offsetof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobMemoryLimit) == 120,
               "unexpected 64-bit job memory limit offset");
_Static_assert(offsetof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION, PeakJobMemoryUsed) == 136,
               "unexpected 64-bit peak job memory offset");
_Static_assert(sizeof(PROCESS_MEMORY_COUNTERS_EX) == 80,
               "unexpected 64-bit process memory layout");
#else
_Static_assert(sizeof(JOBOBJECT_BASIC_LIMIT_INFORMATION) == 44,
               "unexpected 32-bit job limit layout");
_Static_assert(sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION) == 108,
               "unexpected 32-bit extended job limit layout");
_Static_assert(offsetof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobMemoryLimit) == 96,
               "unexpected 32-bit job memory limit offset");
_Static_assert(offsetof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION, PeakJobMemoryUsed) == 104,
               "unexpected 32-bit peak job memory offset");
_Static_assert(sizeof(PROCESS_MEMORY_COUNTERS_EX) == 44,
               "unexpected 32-bit process memory layout");
#endif

enum {
  MIN_TIMEOUT_MS = 100,
  MAX_TIMEOUT_MS = 600000,
  MIN_DRAIN_MS = 0,
  MAX_DRAIN_MS = 30000,
  MAX_ARGUMENTS = 256
};

typedef struct {
  uint64_t limit_bytes;
  DWORD timeout_ms;
  DWORD drain_ms;
  wchar_t *stdout_path;
  wchar_t *stderr_path;
  wchar_t *result_path;
  wchar_t *executable;
  wchar_t *working_directory;
  BOOL affinity_mask_specified;
  DWORD_PTR requested_affinity_mask;
  wchar_t *arguments[MAX_ARGUMENTS];
  size_t argument_count;
} options;

typedef struct {
  BOOL has_exit_code;
  DWORD exit_code;
  BOOL timed_out;
  BOOL job_drain_timed_out;
  BOOL job_assigned;
  BOOL cap_configured;
  BOOL has_peak_job_committed;
  uint64_t peak_job_committed;
  BOOL has_peak_working_set;
  uint64_t peak_working_set;
  BOOL final_working_set_query_succeeded;
  BOOL has_cpu_time;
  uint64_t cpu_time_100ns;
  BOOL has_affinity;
  DWORD_PTR affinity_mask;
  DWORD_PTR available_affinity_mask;
  DWORD selected_cpu_index;
  DWORD peak_sample_count;
  uint64_t elapsed_ms;
  wchar_t error[1024];
} run_result;

static void set_error(run_result *result, const wchar_t *message) {
  if (result->error[0] == L'\0') {
    (void)wcsncpy_s(result->error, _countof(result->error), message, _TRUNCATE);
  }
}

static BOOL parse_u64(const wchar_t *text, uint64_t *value) {
  wchar_t *end = NULL;
  unsigned long long parsed;
  const wchar_t *digit;
  if (text == NULL || text[0] == L'\0' || text[0] == L'-' || text[0] == L'+') {
    return FALSE;
  }
  for (digit = text; *digit != L'\0'; digit++) {
    if (*digit < L'0' || *digit > L'9') {
      return FALSE;
    }
  }
  errno = 0;
  parsed = wcstoull(text, &end, 10);
  if (end == text || *end != L'\0' || errno == ERANGE) {
    return FALSE;
  }
  *value = (uint64_t)parsed;
  return TRUE;
}

static BOOL parse_hex_mask(const wchar_t *text, DWORD_PTR *value) {
  const wchar_t *digit = text;
  uint64_t parsed = 0;
  if (digit == NULL || digit[0] != L'0' || (digit[1] != L'x' && digit[1] != L'X')) {
    return FALSE;
  }
  digit += 2;
  if (*digit == L'\0') {
    return FALSE;
  }
  for (; *digit != L'\0'; digit++) {
    unsigned int nibble;
    if (*digit >= L'0' && *digit <= L'9') {
      nibble = (unsigned int)(*digit - L'0');
    } else if (*digit >= L'a' && *digit <= L'f') {
      nibble = (unsigned int)(*digit - L'a') + 10U;
    } else if (*digit >= L'A' && *digit <= L'F') {
      nibble = (unsigned int)(*digit - L'A') + 10U;
    } else {
      return FALSE;
    }
    if (parsed > (UINT64_MAX - nibble) / 16U) {
      return FALSE;
    }
    parsed = parsed * 16U + nibble;
  }
  if (parsed > (uint64_t)SIZE_MAX) {
    return FALSE;
  }
  *value = (DWORD_PTR)parsed;
  return TRUE;
}

static BOOL is_absolute_path(const wchar_t *path) {
  if (path == NULL || path[0] == L'\0') {
    return FALSE;
  }
  if (path[0] == L'\\' && path[1] == L'\\') {
    return TRUE;
  }
  return ((path[0] >= L'A' && path[0] <= L'Z') ||
          (path[0] >= L'a' && path[0] <= L'z')) &&
         path[1] == L':' && (path[2] == L'\\' || path[2] == L'/');
}

static BOOL parse_options(int argc, wchar_t **argv, options *out) {
  BOOL limit_seen = FALSE;
  BOOL timeout_seen = FALSE;
  BOOL drain_seen = FALSE;
  BOOL stdout_seen = FALSE;
  BOOL stderr_seen = FALSE;
  BOOL result_seen = FALSE;
  BOOL executable_seen = FALSE;
  BOOL working_directory_seen = FALSE;
  BOOL affinity_mask_seen = FALSE;
  int index;

  (void)memset(out, 0, sizeof(*out));
  out->drain_ms = 5000;
  for (index = 1; index < argc; index++) {
    const wchar_t *name = argv[index];
    const wchar_t *value;
    uint64_t number;
    if (wcscmp(name, L"--arg") == 0) {
      if (index + 1 >= argc || out->argument_count >= MAX_ARGUMENTS) {
        fwprintf(stderr, L"--arg requires a value and supports at most %d values.\n",
                 MAX_ARGUMENTS);
        return FALSE;
      }
      out->arguments[out->argument_count++] = argv[++index];
      continue;
    }
    if (index + 1 >= argc) {
      fwprintf(stderr, L"Missing value for %ls.\n", name);
      return FALSE;
    }
    value = argv[++index];
    if (wcscmp(name, L"--limit-bytes") == 0) {
      if (limit_seen || !parse_u64(value, &out->limit_bytes) || out->limit_bytes == 0) {
        fwprintf(stderr, L"--limit-bytes must be a unique positive integer.\n");
        return FALSE;
      }
      limit_seen = TRUE;
    } else if (wcscmp(name, L"--timeout-ms") == 0) {
      if (timeout_seen || !parse_u64(value, &number) || number < MIN_TIMEOUT_MS ||
          number > MAX_TIMEOUT_MS) {
        fwprintf(stderr, L"--timeout-ms must be unique and in %d..%d.\n",
                 MIN_TIMEOUT_MS, MAX_TIMEOUT_MS);
        return FALSE;
      }
      out->timeout_ms = (DWORD)number;
      timeout_seen = TRUE;
    } else if (wcscmp(name, L"--drain-ms") == 0) {
      if (drain_seen || !parse_u64(value, &number) || number > MAX_DRAIN_MS) {
        fwprintf(stderr, L"--drain-ms must be unique and in %d..%d.\n",
                 MIN_DRAIN_MS, MAX_DRAIN_MS);
        return FALSE;
      }
      out->drain_ms = (DWORD)number;
      drain_seen = TRUE;
    } else if (wcscmp(name, L"--stdout") == 0) {
      if (stdout_seen) {
        fwprintf(stderr, L"--stdout may be supplied only once.\n");
        return FALSE;
      }
      out->stdout_path = (wchar_t *)value;
      stdout_seen = TRUE;
    } else if (wcscmp(name, L"--stderr") == 0) {
      if (stderr_seen) {
        fwprintf(stderr, L"--stderr may be supplied only once.\n");
        return FALSE;
      }
      out->stderr_path = (wchar_t *)value;
      stderr_seen = TRUE;
    } else if (wcscmp(name, L"--result") == 0) {
      if (result_seen) {
        fwprintf(stderr, L"--result may be supplied only once.\n");
        return FALSE;
      }
      out->result_path = (wchar_t *)value;
      result_seen = TRUE;
    } else if (wcscmp(name, L"--exe") == 0) {
      if (executable_seen) {
        fwprintf(stderr, L"--exe may be supplied only once.\n");
        return FALSE;
      }
      out->executable = (wchar_t *)value;
      executable_seen = TRUE;
    } else if (wcscmp(name, L"--cwd") == 0) {
      if (working_directory_seen) {
        fwprintf(stderr, L"--cwd may be supplied only once.\n");
        return FALSE;
      }
      out->working_directory = (wchar_t *)value;
      working_directory_seen = TRUE;
    } else if (wcscmp(name, L"--affinity-mask") == 0) {
      if (affinity_mask_seen || !parse_hex_mask(value, &out->requested_affinity_mask) ||
          out->requested_affinity_mask == 0 ||
          (out->requested_affinity_mask & (out->requested_affinity_mask - 1U)) != 0) {
        fwprintf(stderr, L"--affinity-mask must be a unique, nonzero, single-bit hexadecimal mask.\n");
        return FALSE;
      }
      out->affinity_mask_specified = TRUE;
      affinity_mask_seen = TRUE;
    } else {
      fwprintf(stderr, L"Unknown option '%ls'.\n", name);
      return FALSE;
    }
  }

  if (!limit_seen || !timeout_seen || !stdout_seen || !stderr_seen || !result_seen ||
      !executable_seen || out->argument_count == 0) {
    fwprintf(stderr, L"Usage: limit_runner --limit-bytes N --timeout-ms MS "
                     L"[--drain-ms MS] --stdout ABS --stderr ABS --result ABS "
                     L"--exe ABS [--cwd ABS] [--affinity-mask 0xBIT] --arg VALUE [--arg VALUE ...]\n");
    return FALSE;
  }
  if (!is_absolute_path(out->stdout_path) || !is_absolute_path(out->stderr_path) ||
      !is_absolute_path(out->result_path) || !is_absolute_path(out->executable) ||
      (out->working_directory != NULL && !is_absolute_path(out->working_directory))) {
    fwprintf(stderr, L"Executable, output, result, and working-directory paths must be absolute.\n");
    return FALSE;
  }
  if (_wcsicmp(out->stdout_path, out->stderr_path) == 0 ||
      _wcsicmp(out->stdout_path, out->result_path) == 0 ||
      _wcsicmp(out->stderr_path, out->result_path) == 0) {
    fwprintf(stderr, L"Output, error, and result paths must be distinct.\n");
    return FALSE;
  }
  return TRUE;
}

static BOOL reserve_wide(wchar_t **buffer, size_t *capacity, size_t required) {
  wchar_t *next;
  size_t new_capacity;
  if (required <= *capacity) {
    return TRUE;
  }
  new_capacity = *capacity == 0 ? 256 : *capacity;
  while (new_capacity < required) {
    if (new_capacity > (SIZE_MAX / sizeof(wchar_t)) / 2) {
      return FALSE;
    }
    new_capacity *= 2;
  }
  next = (wchar_t *)realloc(*buffer, new_capacity * sizeof(wchar_t));
  if (next == NULL) {
    return FALSE;
  }
  *buffer = next;
  *capacity = new_capacity;
  return TRUE;
}

static BOOL append_wide(wchar_t **buffer, size_t *length, size_t *capacity,
                        const wchar_t *text, size_t count) {
  if (!reserve_wide(buffer, capacity, *length + count + 1)) {
    return FALSE;
  }
  (void)memcpy(*buffer + *length, text, count * sizeof(wchar_t));
  *length += count;
  (*buffer)[*length] = L'\0';
  return TRUE;
}

static BOOL append_quoted_argument(wchar_t **buffer, size_t *length, size_t *capacity,
                                  const wchar_t *argument) {
  size_t index = 0;
  size_t slash_count = 0;
  if (!append_wide(buffer, length, capacity, L"\"", 1)) {
    return FALSE;
  }
  for (;;) {
    wchar_t current = argument[index];
    if (current == L'\\') {
      slash_count++;
      index++;
      continue;
    }
    if (current == L'\"') {
      size_t escape_count = slash_count * 2 + 1;
      size_t escape_index;
      for (escape_index = 0; escape_index < escape_count; escape_index++) {
        if (!append_wide(buffer, length, capacity, L"\\", 1)) {
          return FALSE;
        }
      }
      if (!append_wide(buffer, length, capacity, L"\"", 1)) {
        return FALSE;
      }
    } else {
      size_t slash_index;
      size_t emitted_slashes = current == L'\0' ? slash_count * 2 : slash_count;
      for (slash_index = 0; slash_index < emitted_slashes; slash_index++) {
        if (!append_wide(buffer, length, capacity, L"\\", 1)) {
          return FALSE;
        }
      }
      if (current == L'\0') {
        break;
      }
      if (!append_wide(buffer, length, capacity, &current, 1)) {
        return FALSE;
      }
    }
    slash_count = 0;
    index++;
  }
  return append_wide(buffer, length, capacity, L"\"", 1);
}

static wchar_t *make_command_line(const options *opts) {
  wchar_t *buffer = NULL;
  size_t length = 0;
  size_t capacity = 0;
  size_t index;
  if (!append_quoted_argument(&buffer, &length, &capacity, opts->executable)) {
    free(buffer);
    return NULL;
  }
  for (index = 0; index < opts->argument_count; index++) {
    if (!append_wide(&buffer, &length, &capacity, L" ", 1) ||
        !append_quoted_argument(&buffer, &length, &capacity, opts->arguments[index])) {
      free(buffer);
      return NULL;
    }
  }
  return buffer;
}

static BOOL write_json_string(FILE *file, const wchar_t *value) {
  int bytes_required;
  char *utf8;
  int index;
  if (value == NULL) {
    return fputs("null", file) >= 0;
  }
  bytes_required = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, NULL, 0, NULL, NULL);
  if (bytes_required <= 0) {
    return FALSE;
  }
  utf8 = (char *)malloc((size_t)bytes_required);
  if (utf8 == NULL) {
    return FALSE;
  }
  if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, utf8,
                          bytes_required, NULL, NULL) <= 0) {
    free(utf8);
    return FALSE;
  }
  if (fputc('"', file) == EOF) {
    free(utf8);
    return FALSE;
  }
  for (index = 0; index < bytes_required - 1; index++) {
    unsigned char current = (unsigned char)utf8[index];
    if (current == '"' || current == '\\') {
      if (fputc('\\', file) == EOF || fputc(current, file) == EOF) {
        free(utf8);
        return FALSE;
      }
    } else if (current < 0x20) {
      if (fprintf(file, "\\u%04x", (unsigned int)current) < 0) {
        free(utf8);
        return FALSE;
      }
    } else if (fputc(current, file) == EOF) {
      free(utf8);
      return FALSE;
    }
  }
  free(utf8);
  return fputc('"', file) != EOF;
}

static BOOL write_result(const options *opts, const run_result *result) {
  FILE *file = NULL;
  BOOL okay;
  if (_wfopen_s(&file, opts->result_path, L"wb") != 0 || file == NULL) {
    fwprintf(stderr, L"Could not open runner result file '%ls'.\n", opts->result_path);
    return FALSE;
  }
  okay = fputs("{\"schemaVersion\":1,\"memoryLimitScope\":\"jobProcessCommitIncludingDescendants\",\"enforcedJobMemoryLimitBytes\":", file) >= 0 &&
         fprintf(file, "%llu", (unsigned long long)opts->limit_bytes) >= 0 &&
         fputs(",\"timeoutMs\":", file) >= 0 && fprintf(file, "%lu", (unsigned long)opts->timeout_ms) >= 0 &&
         fputs(",\"drainMs\":", file) >= 0 && fprintf(file, "%lu", (unsigned long)opts->drain_ms) >= 0 &&
         fputs(",\"executable\":", file) >= 0 && write_json_string(file, opts->executable) &&
         fputs(",\"workingDirectory\":", file) >= 0 && write_json_string(file, opts->working_directory) &&
         fputs(",\"exitCode\":", file) >= 0;
  if (okay) {
    okay = result->has_exit_code ? fprintf(file, "%lu", (unsigned long)result->exit_code) >= 0
                                 : fputs("null", file) >= 0;
  }
  okay = okay && fputs(",\"timedOut\":", file) >= 0 && fputs(result->timed_out ? "true" : "false", file) >= 0 &&
         fputs(",\"jobDrainTimedOut\":", file) >= 0 && fputs(result->job_drain_timed_out ? "true" : "false", file) >= 0 &&
         fputs(",\"jobAssigned\":", file) >= 0 && fputs(result->job_assigned ? "true" : "false", file) >= 0 &&
         fputs(",\"capConfigured\":", file) >= 0 && fputs(result->cap_configured ? "true" : "false", file) >= 0 &&
         fputs(",\"peakJobCommittedBytes\":", file) >= 0;
  if (okay) {
    okay = result->has_peak_job_committed
               ? fprintf(file, "%llu", (unsigned long long)result->peak_job_committed) >= 0
               : fputs("null", file) >= 0;
  }
  okay = okay && fputs(",\"peakWorkingSetBytes\":", file) >= 0;
  if (okay) {
    okay = result->has_peak_working_set
               ? fprintf(file, "%llu", (unsigned long long)result->peak_working_set) >= 0
               : fputs("null", file) >= 0;
  }
  okay = okay && fputs(",\"finalWorkingSetQuerySucceeded\":", file) >= 0 &&
         fputs(result->final_working_set_query_succeeded ? "true" : "false", file) >= 0 &&
         fputs(",\"peakWorkingSetIsSampledLowerBound\":", file) >= 0 &&
         fputs(result->final_working_set_query_succeeded ? "false" : "true", file) >= 0;
  okay = okay && fputs(",\"cpuTime100ns\":", file) >= 0;
  if (okay) {
    okay = result->has_cpu_time ? fprintf(file, "%llu", (unsigned long long)result->cpu_time_100ns) >= 0
                                : fputs("null", file) >= 0;
  }
  okay = okay && fputs(",\"affinityMask\":", file) >= 0;
  if (okay) {
    if (result->has_affinity) {
      okay = fprintf(file, "\"0x%llx\"", (unsigned long long)result->affinity_mask) >= 0;
    } else {
      okay = fputs("null", file) >= 0;
    }
  }
  okay = okay && fputs(",\"availableAffinityMask\":", file) >= 0;
  if (okay) {
    okay = result->has_affinity
               ? fprintf(file, "\"0x%llx\"", (unsigned long long)result->available_affinity_mask) >= 0
               : fputs("null", file) >= 0;
  }
  okay = okay && fputs(",\"selectedCpuIndex\":", file) >= 0;
  if (okay) {
    okay = result->has_affinity ? fprintf(file, "%lu", (unsigned long)result->selected_cpu_index) >= 0
                                : fputs("null", file) >= 0;
  }
  okay = okay && fputs(",\"peakSampleCount\":", file) >= 0 &&
         fprintf(file, "%lu", (unsigned long)result->peak_sample_count) >= 0 &&
         fputs(",\"elapsedMilliseconds\":", file) >= 0 &&
         fprintf(file, "%llu", (unsigned long long)result->elapsed_ms) >= 0 &&
         fputs(",\"error\":", file) >= 0 && write_json_string(file, result->error[0] == L'\0' ? NULL : result->error) &&
         fputs("}\n", file) >= 0;
  if (fclose(file) != 0) {
    okay = FALSE;
  }
  return okay;
}

static DWORD lowest_bit_index(DWORD_PTR value) {
  DWORD index = 0;
  while (index < (DWORD)(sizeof(value) * 8U) && (value & ((DWORD_PTR)1U << index)) == 0) {
    index++;
  }
  return index;
}

static BOOL sample_peak_working_set(HANDLE process, run_result *result) {
  PROCESS_MEMORY_COUNTERS_EX counters;
  (void)memset(&counters, 0, sizeof(counters));
  counters.cb = sizeof(counters);
  if (GetProcessMemoryInfo(process, (PROCESS_MEMORY_COUNTERS *)&counters, sizeof(counters))) {
    uint64_t peak = (uint64_t)counters.PeakWorkingSetSize;
    if (!result->has_peak_working_set || peak > result->peak_working_set) {
      result->peak_working_set = peak;
      result->has_peak_working_set = TRUE;
    }
    result->peak_sample_count++;
    return TRUE;
  }
  return FALSE;
}

static BOOL query_job_peak(HANDLE job, run_result *result) {
  JOBOBJECT_EXTENDED_LIMIT_INFORMATION info;
  (void)memset(&info, 0, sizeof(info));
  if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformation, &info,
                                 sizeof(info), NULL)) {
    return FALSE;
  }
  result->peak_job_committed = (uint64_t)info.PeakJobMemoryUsed;
  result->has_peak_job_committed = TRUE;
  return TRUE;
}

static BOOL query_job_active_processes(HANDLE job, DWORD *active) {
  JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info;
  (void)memset(&info, 0, sizeof(info));
  if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, &info,
                                 sizeof(info), NULL)) {
    return FALSE;
  }
  *active = info.ActiveProcesses;
  return TRUE;
}

static void query_cpu_time(HANDLE process, run_result *result) {
  FILETIME creation;
  FILETIME exit;
  FILETIME kernel;
  FILETIME user;
  ULARGE_INTEGER kernel_value;
  ULARGE_INTEGER user_value;
  if (GetProcessTimes(process, &creation, &exit, &kernel, &user)) {
    kernel_value.LowPart = kernel.dwLowDateTime;
    kernel_value.HighPart = kernel.dwHighDateTime;
    user_value.LowPart = user.dwLowDateTime;
    user_value.HighPart = user.dwHighDateTime;
    result->cpu_time_100ns = (uint64_t)kernel_value.QuadPart + (uint64_t)user_value.QuadPart;
    result->has_cpu_time = TRUE;
  }
}

static BOOL run_child(const options *opts, run_result *result) {
  HANDLE job = NULL;
  HANDLE stdout_handle = INVALID_HANDLE_VALUE;
  HANDLE stderr_handle = INVALID_HANDLE_VALUE;
  PROCESS_INFORMATION process_info;
  STARTUPINFOW startup;
  JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits;
  wchar_t *command_line = NULL;
  DWORD_PTR process_mask = 0;
  DWORD_PTR system_mask = 0;
  DWORD_PTR selected_mask;
  DWORD wait_result;
  DWORD started_tick = GetTickCount();
  BOOL okay = FALSE;
  DWORD active_processes = 0;
  DWORD drain_started_tick = 0;
  BOOL process_has_exited = FALSE;
  DWORD poll_interval = 20;

  (void)memset(&process_info, 0, sizeof(process_info));
  (void)memset(&startup, 0, sizeof(startup));
  (void)memset(&limits, 0, sizeof(limits));
  startup.cb = sizeof(startup);
  startup.dwFlags = STARTF_USESTDHANDLES;
  job = CreateJobObjectW(NULL, NULL);
  if (job == NULL) {
    set_error(result, L"CreateJobObjectW failed.");
    goto cleanup;
  }
  limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_JOB_MEMORY |
                                           JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
  if (opts->limit_bytes > (uint64_t)SIZE_MAX) {
    set_error(result, L"The requested memory limit does not fit SIZE_T.");
    goto cleanup;
  }
  limits.JobMemoryLimit = (SIZE_T)opts->limit_bytes;
  if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) {
    set_error(result, L"SetInformationJobObject failed while enforcing the job memory limit.");
    goto cleanup;
  }
  result->cap_configured = TRUE;
  stdout_handle = CreateFileW(opts->stdout_path, GENERIC_WRITE, FILE_SHARE_READ, NULL,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
  if (stdout_handle == INVALID_HANDLE_VALUE) {
    set_error(result, L"Could not create the child stdout capture file.");
    goto cleanup;
  }
  stderr_handle = CreateFileW(opts->stderr_path, GENERIC_WRITE, FILE_SHARE_READ, NULL,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
  if (stderr_handle == INVALID_HANDLE_VALUE) {
    set_error(result, L"Could not create the child stderr capture file.");
    goto cleanup;
  }
  startup.hStdOutput = stdout_handle;
  startup.hStdError = stderr_handle;
  startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
  if (!SetHandleInformation(stdout_handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT) ||
      !SetHandleInformation(stderr_handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) {
    set_error(result, L"Could not make the child capture handles inheritable.");
    goto cleanup;
  }
  command_line = make_command_line(opts);
  if (command_line == NULL) {
    set_error(result, L"Could not allocate the child command line.");
    goto cleanup;
  }
  if (!GetProcessAffinityMask(GetCurrentProcess(), &process_mask, &system_mask)) {
    set_error(result, L"GetProcessAffinityMask failed for the launcher.");
    goto cleanup;
  }
  result->available_affinity_mask = process_mask & system_mask;
  if (result->available_affinity_mask == 0) {
    set_error(result, L"The launcher has no available processor in its affinity mask.");
    goto cleanup;
  }
  selected_mask = opts->affinity_mask_specified
                      ? opts->requested_affinity_mask
                      : result->available_affinity_mask & (~result->available_affinity_mask + 1U);
  if ((selected_mask & result->available_affinity_mask) != selected_mask) {
    set_error(result, L"The requested affinity mask is not available to the launcher.");
    goto cleanup;
  }
  result->affinity_mask = selected_mask;
  result->selected_cpu_index = lowest_bit_index(selected_mask);
  result->has_affinity = TRUE;
  if (!CreateProcessW(opts->executable, command_line, NULL, NULL, TRUE,
                      CREATE_SUSPENDED | CREATE_NO_WINDOW, NULL,
                      opts->working_directory, &startup, &process_info)) {
    set_error(result, L"CreateProcessW failed.");
    goto cleanup;
  }
  if (!AssignProcessToJobObject(job, process_info.hProcess)) {
    set_error(result, L"AssignProcessToJobObject failed; the child was never resumed.");
    (void)TerminateProcess(process_info.hProcess, ERROR_ACCESS_DENIED);
    (void)WaitForSingleObject(process_info.hProcess, 5000);
    goto cleanup;
  }
  result->job_assigned = TRUE;
  if (!SetProcessAffinityMask(process_info.hProcess, selected_mask)) {
    set_error(result, L"SetProcessAffinityMask failed; the child was never resumed.");
    (void)TerminateJobObject(job, ERROR_INVALID_PARAMETER);
    (void)WaitForSingleObject(process_info.hProcess, 5000);
    goto cleanup;
  }
  if (ResumeThread(process_info.hThread) == (DWORD)-1) {
    set_error(result, L"ResumeThread failed; the job was terminated.");
    (void)TerminateJobObject(job, ERROR_INVALID_FUNCTION);
    (void)WaitForSingleObject(process_info.hProcess, 5000);
    goto cleanup;
  }

  (void)sample_peak_working_set(process_info.hProcess, result);
  while (!process_has_exited) {
    wait_result = WaitForSingleObject(process_info.hProcess, poll_interval);
    (void)sample_peak_working_set(process_info.hProcess, result);
    if (wait_result == WAIT_OBJECT_0) {
      process_has_exited = TRUE;
      break;
    }
    if (wait_result == WAIT_FAILED) {
      set_error(result, L"WaitForSingleObject failed while waiting for the child.");
      (void)TerminateJobObject(job, ERROR_INVALID_FUNCTION);
      (void)WaitForSingleObject(process_info.hProcess, 5000);
      process_has_exited = TRUE;
      break;
    }
    if ((DWORD)(GetTickCount() - started_tick) >= opts->timeout_ms) {
      result->timed_out = TRUE;
      (void)TerminateJobObject(job, WAIT_TIMEOUT);
      if (WaitForSingleObject(process_info.hProcess, 5000) != WAIT_OBJECT_0) {
        set_error(result, L"The timed-out job did not stop within the 5000 ms termination wait.");
      }
      process_has_exited = TRUE;
      break;
    }
  }

  if (process_has_exited && !result->timed_out) {
    drain_started_tick = GetTickCount();
    for (;;) {
      if (!query_job_active_processes(job, &active_processes)) {
        set_error(result, L"QueryInformationJobObject failed while checking child-process drain.");
        break;
      }
      if (active_processes == 0) {
        break;
      }
      if ((DWORD)(GetTickCount() - drain_started_tick) >= opts->drain_ms) {
        result->job_drain_timed_out = TRUE;
        set_error(result, L"Child processes remained in the job after the bounded drain wait.");
        (void)TerminateJobObject(job, WAIT_TIMEOUT);
        (void)WaitForSingleObject(process_info.hProcess, 5000);
        break;
      }
      Sleep(poll_interval);
    }
  }

  result->final_working_set_query_succeeded = sample_peak_working_set(process_info.hProcess, result);
  if (!result->final_working_set_query_succeeded && !result->has_peak_working_set) {
    set_error(result, L"Peak working set could not be sampled before or after process exit.");
  }
  if (GetExitCodeProcess(process_info.hProcess, &result->exit_code)) {
    result->has_exit_code = TRUE;
  } else {
    set_error(result, L"GetExitCodeProcess failed.");
  }
  query_cpu_time(process_info.hProcess, result);
  if (!query_job_peak(job, result)) {
    set_error(result, L"QueryInformationJobObject failed while reading peak committed bytes.");
  }
  okay = result->error[0] == L'\0';

cleanup:
  result->elapsed_ms = (uint64_t)(DWORD)(GetTickCount() - started_tick);
  if (job != NULL && result->job_assigned) {
    (void)query_job_peak(job, result);
  }
  if (process_info.hThread != NULL) {
    CloseHandle(process_info.hThread);
  }
  if (process_info.hProcess != NULL) {
    CloseHandle(process_info.hProcess);
  }
  if (stdout_handle != INVALID_HANDLE_VALUE) {
    CloseHandle(stdout_handle);
  }
  if (stderr_handle != INVALID_HANDLE_VALUE) {
    CloseHandle(stderr_handle);
  }
  if (job != NULL) {
    CloseHandle(job);
  }
  free(command_line);
  return okay;
}

int wmain(int argc, wchar_t **argv) {
  options opts;
  run_result result;
  BOOL okay;
  (void)memset(&result, 0, sizeof(result));
  if (!parse_options(argc, argv, &opts)) {
    return 2;
  }
  okay = run_child(&opts, &result);
  if (!write_result(&opts, &result)) {
    return 3;
  }
  if (!okay) {
    fwprintf(stderr, L"limit_runner: %ls\n", result.error);
    return 1;
  }
  return 0;
}
