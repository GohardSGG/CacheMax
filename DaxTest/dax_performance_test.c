#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define TEST_SIZE (32 * 1024 * 1024)      // 32MB test file
#define BLOCK_SIZE (4 * 1024)             // 4KB blocks
#define NUM_OPERATIONS 1000                // Number of random operations

typedef struct {
    const char* method_name;
    double read_speed_mbps;
    double write_speed_mbps;
} PerformanceResult;

// High precision timer
static LARGE_INTEGER frequency;

void InitTimer() {
    QueryPerformanceFrequency(&frequency);
}

double GetElapsedSeconds(LARGE_INTEGER start, LARGE_INTEGER end) {
    return (double)(end.QuadPart - start.QuadPart) / frequency.QuadPart;
}

// Method 1: Traditional ReadFile/WriteFile with 4K random operations
PerformanceResult TestTraditionalIO(const char* test_file) {
    PerformanceResult result = { "Traditional ReadFile/WriteFile (4K Random)", 0.0, 0.0 };
    HANDLE hFile;
    DWORD bytesTransferred;
    LARGE_INTEGER start, end;
    char* buffer = (char*)malloc(BLOCK_SIZE);

    if (!buffer) return result;

    // Fill buffer with test data
    memset(buffer, 0xAA, BLOCK_SIZE);

    // Create test file first
    hFile = CreateFileA(test_file, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS,
                       FILE_ATTRIBUTE_NORMAL, NULL);

    if (hFile != INVALID_HANDLE_VALUE) {
        // Pre-fill the file to TEST_SIZE
        char* large_buffer = (char*)malloc(1024 * 1024); // 1MB buffer for faster pre-fill
        if (large_buffer) {
            memset(large_buffer, 0xAA, 1024 * 1024);
            for (int i = 0; i < TEST_SIZE / (1024 * 1024); i++) {
                WriteFile(hFile, large_buffer, 1024 * 1024, &bytesTransferred, NULL);
            }
            free(large_buffer);
        }
        FlushFileBuffers(hFile);
        CloseHandle(hFile);
    }

    // Test Random Write Speed (4K blocks)
    hFile = CreateFileA(test_file, GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
                       FILE_ATTRIBUTE_NORMAL, NULL);

    if (hFile != INVALID_HANDLE_VALUE) {
        srand((unsigned int)time(NULL));
        QueryPerformanceCounter(&start);

        for (int i = 0; i < NUM_OPERATIONS; i++) {
            // Random offset aligned to 4K boundary
            UINT64 offset = (rand() % (TEST_SIZE / BLOCK_SIZE)) * BLOCK_SIZE;

            OVERLAPPED overlapped = { 0 };
            overlapped.Offset = (DWORD)offset;
            overlapped.OffsetHigh = (DWORD)(offset >> 32);

            if (!WriteFile(hFile, buffer, BLOCK_SIZE, &bytesTransferred, &overlapped)) {
                if (GetLastError() != ERROR_IO_PENDING) break;
            }
        }
        FlushFileBuffers(hFile);

        QueryPerformanceCounter(&end);
        CloseHandle(hFile);

        double elapsed = GetElapsedSeconds(start, end);
        double total_mb = (NUM_OPERATIONS * BLOCK_SIZE) / (1024.0 * 1024.0);
        result.write_speed_mbps = total_mb / elapsed;
    }

    // Test Random Read Speed (4K blocks)
    hFile = CreateFileA(test_file, GENERIC_READ, 0, NULL, OPEN_EXISTING,
                       FILE_ATTRIBUTE_NORMAL, NULL);

    if (hFile != INVALID_HANDLE_VALUE) {
        srand((unsigned int)time(NULL) + 1000);
        QueryPerformanceCounter(&start);

        for (int i = 0; i < NUM_OPERATIONS; i++) {
            // Random offset aligned to 4K boundary
            UINT64 offset = (rand() % (TEST_SIZE / BLOCK_SIZE)) * BLOCK_SIZE;

            OVERLAPPED overlapped = { 0 };
            overlapped.Offset = (DWORD)offset;
            overlapped.OffsetHigh = (DWORD)(offset >> 32);

            if (!ReadFile(hFile, buffer, BLOCK_SIZE, &bytesTransferred, &overlapped)) {
                if (GetLastError() != ERROR_IO_PENDING) break;
            }
        }

        QueryPerformanceCounter(&end);
        CloseHandle(hFile);

        double elapsed = GetElapsedSeconds(start, end);
        double total_mb = (NUM_OPERATIONS * BLOCK_SIZE) / (1024.0 * 1024.0);
        result.read_speed_mbps = total_mb / elapsed;
    }

    free(buffer);
    return result;
}

// Method 2: Memory Mapped Files (DAX zero-copy) with 4K random operations
PerformanceResult TestMemoryMapped(const char* test_file) {
    PerformanceResult result = { "Memory Mapped (DAX Zero-Copy 4K Random)", 0.0, 0.0 };
    HANDLE hFile, hMapping;
    LPVOID pView;
    LARGE_INTEGER start, end;
    char* buffer = (char*)malloc(BLOCK_SIZE);

    if (!buffer) return result;

    // Fill buffer with test data
    memset(buffer, 0xBB, BLOCK_SIZE);

    // Create test file first
    hFile = CreateFileA(test_file, GENERIC_READ | GENERIC_WRITE, 0, NULL,
                       CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);

    if (hFile == INVALID_HANDLE_VALUE) {
        free(buffer);
        return result;
    }

    // Set file size
    LARGE_INTEGER fileSize;
    fileSize.QuadPart = TEST_SIZE;
    SetFilePointerEx(hFile, fileSize, NULL, FILE_BEGIN);
    SetEndOfFile(hFile);

    // Create memory mapping
    hMapping = CreateFileMapping(hFile, NULL, PAGE_READWRITE, 0, 0, NULL);
    if (!hMapping) {
        CloseHandle(hFile);
        free(buffer);
        return result;
    }

    pView = MapViewOfFile(hMapping, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (!pView) {
        CloseHandle(hMapping);
        CloseHandle(hFile);
        free(buffer);
        return result;
    }

    char* mapped_ptr = (char*)pView;

    // Test Random Write Speed (zero-copy, 4K blocks)
    srand((unsigned int)time(NULL) + 2000);
    QueryPerformanceCounter(&start);

    for (int i = 0; i < NUM_OPERATIONS; i++) {
        // Random offset aligned to 4K boundary
        size_t offset = (rand() % (TEST_SIZE / BLOCK_SIZE)) * BLOCK_SIZE;

        // ZERO-COPY WRITE: Direct memory copy to DAX PMem!
        memcpy(mapped_ptr + offset, buffer, BLOCK_SIZE);
    }
    FlushViewOfFile(pView, TEST_SIZE);

    QueryPerformanceCounter(&end);

    double elapsed = GetElapsedSeconds(start, end);
    double total_mb = (NUM_OPERATIONS * BLOCK_SIZE) / (1024.0 * 1024.0);
    result.write_speed_mbps = total_mb / elapsed;

    // Test Random Read Speed (zero-copy, 4K blocks)
    srand((unsigned int)time(NULL) + 3000);
    QueryPerformanceCounter(&start);

    for (int i = 0; i < NUM_OPERATIONS; i++) {
        // Random offset aligned to 4K boundary
        size_t offset = (rand() % (TEST_SIZE / BLOCK_SIZE)) * BLOCK_SIZE;

        // ZERO-COPY READ: Direct memory copy from DAX PMem!
        memcpy(buffer, mapped_ptr + offset, BLOCK_SIZE);
    }

    QueryPerformanceCounter(&end);

    elapsed = GetElapsedSeconds(start, end);
    total_mb = (NUM_OPERATIONS * BLOCK_SIZE) / (1024.0 * 1024.0);
    result.read_speed_mbps = total_mb / elapsed;

    // Cleanup
    UnmapViewOfFile(pView);
    CloseHandle(hMapping);
    CloseHandle(hFile);
    free(buffer);

    return result;
}

// Method 3: Direct memory access (simulate zero-copy)
PerformanceResult TestDirectMemory(const char* test_file) {
    PerformanceResult result = { "Direct Memory Access", 0.0, 0.0 };
    LARGE_INTEGER start, end;
    char* memory_block = (char*)malloc(TEST_SIZE);
    char* buffer = (char*)malloc(BLOCK_SIZE);

    if (!memory_block || !buffer) {
        if (memory_block) free(memory_block);
        if (buffer) free(buffer);
        return result;
    }

    memset(buffer, 0xCC, BLOCK_SIZE);

    // Test Write Speed (direct memory)
    QueryPerformanceCounter(&start);

    for (int i = 0; i < TEST_SIZE / BLOCK_SIZE; i++) {
        memcpy(memory_block + (i * BLOCK_SIZE), buffer, BLOCK_SIZE);
    }

    QueryPerformanceCounter(&end);

    double elapsed = GetElapsedSeconds(start, end);
    result.write_speed_mbps = (TEST_SIZE / (1024.0 * 1024.0)) / elapsed;

    // Test Read Speed (direct memory)
    QueryPerformanceCounter(&start);

    for (int i = 0; i < TEST_SIZE / BLOCK_SIZE; i++) {
        memcpy(buffer, memory_block + (i * BLOCK_SIZE), BLOCK_SIZE);
    }

    QueryPerformanceCounter(&end);

    elapsed = GetElapsedSeconds(start, end);
    result.read_speed_mbps = (TEST_SIZE / (1024.0 * 1024.0)) / elapsed;

    free(memory_block);
    free(buffer);
    return result;
}

int main() {
    printf("=== DAX vs Regular Disk Performance Test ===\n");
    printf("Test Size: %d MB, Block Size: %d KB, Operations: %d\n",
           TEST_SIZE / (1024 * 1024), BLOCK_SIZE / 1024, NUM_OPERATIONS);
    printf("Mode: 4K Random Read/Write\n");
    printf("Target: 1000+ MB/s on DAX-enabled PMem\n\n");

    InitTimer();

    // Test on regular disk A: (normal SSD/HDD)
    printf("=== Testing Regular Disk (A:) ===\n");
    PerformanceResult a_traditional = TestTraditionalIO("A:\\temp_test_regular.dat");
    PerformanceResult a_mapped = TestMemoryMapped("A:\\temp_test_mapped.dat");

    printf("Regular Disk (A:) - Traditional I/O (4K Random):\n");
    printf("  Read:  %.2f MB/s\n", a_traditional.read_speed_mbps);
    printf("  Write: %.2f MB/s\n\n", a_traditional.write_speed_mbps);

    printf("Regular Disk (A:) - Memory Mapped (4K Random):\n");
    printf("  Read:  %.2f MB/s\n", a_mapped.read_speed_mbps);
    printf("  Write: %.2f MB/s\n\n", a_mapped.write_speed_mbps);

    // Test on DAX volume S: (PMem with DAX)
    printf("=== Testing DAX Volume (S:) ===\n");
    PerformanceResult s_traditional = TestTraditionalIO("S:\\temp_test_regular.dat");
    PerformanceResult s_mapped = TestMemoryMapped("S:\\temp_test_mapped.dat");

    printf("DAX Volume (S:) - Traditional I/O (4K Random):\n");
    printf("  Read:  %.2f MB/s\n", s_traditional.read_speed_mbps);
    printf("  Write: %.2f MB/s\n\n", s_traditional.write_speed_mbps);

    printf("DAX Volume (S:) - Memory Mapped ZERO-COPY (4K Random):\n");
    printf("  Read:  %.2f MB/s\n", s_mapped.read_speed_mbps);
    printf("  Write: %.2f MB/s\n\n", s_mapped.write_speed_mbps);

    // Performance Analysis
    printf("=== Performance Analysis ===\n");

    // DAX vs Regular comparison
    if (a_traditional.read_speed_mbps > 0) {
        double dax_vs_regular_read = s_mapped.read_speed_mbps / a_traditional.read_speed_mbps;
        double dax_vs_regular_write = s_mapped.write_speed_mbps / a_traditional.write_speed_mbps;

        printf("DAX Zero-Copy vs Regular Disk Traditional I/O:\n");
        printf("  Read speedup:  %.2fx\n", dax_vs_regular_read);
        printf("  Write speedup: %.2fx\n\n", dax_vs_regular_write);
    }

    // Memory mapping improvement on DAX
    if (s_traditional.read_speed_mbps > 0) {
        double dax_improvement_read = s_mapped.read_speed_mbps / s_traditional.read_speed_mbps;
        double dax_improvement_write = s_mapped.write_speed_mbps / s_traditional.write_speed_mbps;

        printf("Memory Mapped vs Traditional on DAX Volume:\n");
        printf("  Read improvement:  %.2fx\n", dax_improvement_read);
        printf("  Write improvement: %.2fx\n\n", dax_improvement_write);
    }

    // Success criteria
    printf("=== Results ===\n");
    if (s_mapped.read_speed_mbps > 500.0) {
        printf("✅ SUCCESS: DAX memory mapping achieved excellent speed (%.0f MB/s)!\n", s_mapped.read_speed_mbps);
    } else if (s_mapped.read_speed_mbps > 100.0) {
        printf("⚠️  GOOD: DAX speed decent but has room for improvement (%.0f MB/s)\n", s_mapped.read_speed_mbps);
    } else {
        printf("❌ NEED WORK: DAX speed below expectations (%.0f MB/s)\n", s_mapped.read_speed_mbps);
    }

    printf("\nThis test demonstrates the potential of DAX zero-copy access.\n");
    printf("When WinFsp uses memory mapping, it should achieve similar performance!\n");

    // Cleanup test files
    DeleteFileA("A:\\temp_test_regular.dat");
    DeleteFileA("A:\\temp_test_mapped.dat");
    DeleteFileA("S:\\temp_test_regular.dat");
    DeleteFileA("S:\\temp_test_mapped.dat");

    return 0;
}