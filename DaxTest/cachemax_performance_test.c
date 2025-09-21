#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define TEST_SIZE (32 * 1024 * 1024)      // 32MB test file
#define BLOCK_SIZE (4 * 1024)             // 4KB blocks
#define NUM_OPERATIONS 1000                // Number of random operations

// High precision timer
static LARGE_INTEGER frequency;

void InitTimer() {
    QueryPerformanceFrequency(&frequency);
}

double GetElapsedSeconds(LARGE_INTEGER start, LARGE_INTEGER end) {
    return (double)(end.QuadPart - start.QuadPart) / frequency.QuadPart;
}

// Test CacheMax performance using the same method as DAX test
double TestCacheMaxPerformance(const char* test_file) {
    HANDLE hFile;
    DWORD bytesTransferred;
    LARGE_INTEGER start, end;
    char* buffer = (char*)malloc(BLOCK_SIZE);
    double read_speed_mbps = 0.0;

    if (!buffer) return 0.0;

    printf("Testing CacheMax performance on: %s\n", test_file);

    // Test Random Read Speed (4K blocks) - same as DAX test
    hFile = CreateFileA(test_file, GENERIC_READ, 0, NULL, OPEN_EXISTING,
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

            if (!ReadFile(hFile, buffer, BLOCK_SIZE, &bytesTransferred, &overlapped)) {
                if (GetLastError() != ERROR_IO_PENDING) break;
            }
        }

        QueryPerformanceCounter(&end);
        CloseHandle(hFile);

        double elapsed = GetElapsedSeconds(start, end);
        double total_mb = (NUM_OPERATIONS * BLOCK_SIZE) / (1024.0 * 1024.0);
        read_speed_mbps = total_mb / elapsed;

        printf("  Random 4K Read: %.2f MB/s (%.4f seconds for %d operations)\n",
               read_speed_mbps, elapsed, NUM_OPERATIONS);
    }

    free(buffer);
    return read_speed_mbps;
}

int main() {
    printf("=== CacheMax Performance Test (Same Method as DAX Test) ===\n");
    printf("Test Size: %d MB, Block Size: %d KB, Operations: %d\n",
           TEST_SIZE / (1024 * 1024), BLOCK_SIZE / 1024, NUM_OPERATIONS);
    printf("Using identical testing method to DAX performance test\n\n");

    InitTimer();

    // Test original disk performance (A:)
    printf("=== Testing Original Disk (A:\\Test\\benchmark_test.dat) ===\n");
    double original_speed = TestCacheMaxPerformance("A:\\Test\\benchmark_test.dat");

    // Test direct cache disk performance (S:)
    printf("\n=== Testing Direct Cache Disk (S:\\Cache\\Test\\benchmark_test.dat) ===\n");
    double cache_speed = TestCacheMaxPerformance("S:\\Cache\\Test\\benchmark_test.dat");

    // Test CacheMax mounted performance (A: via WinFsp)
    printf("\n=== Testing CacheMax Mounted (A:\\Test\\benchmark_test.dat via WinFsp) ===\n");
    printf("Note: Make sure CacheMax is running with A:\\Test mounted!\n");
    double cachemax_speed = TestCacheMaxPerformance("A:\\Test\\benchmark_test.dat");

    // Analysis
    printf("\n=== Performance Analysis ===\n");
    if (original_speed > 0) {
        printf("Original disk speed: %.2f MB/s\n", original_speed);
        printf("Cache disk speed: %.2f MB/s (%.2fx speedup)\n",
               cache_speed, cache_speed / original_speed);
        printf("CacheMax speed: %.2f MB/s (%.2fx speedup vs original)\n",
               cachemax_speed, cachemax_speed / original_speed);

        if (cache_speed > 0) {
            double efficiency = cachemax_speed / cache_speed;
            printf("CacheMax efficiency: %.1f%% of direct cache access\n", efficiency * 100);

            if (efficiency > 0.8) {
                printf("✅ EXCELLENT: CacheMax is highly optimized!\n");
            } else if (efficiency > 0.5) {
                printf("⚠️  GOOD: CacheMax has room for improvement\n");
            } else {
                printf("❌ NEEDS WORK: CacheMax has significant overhead\n");
            }
        }
    }

    printf("\nThis test uses the exact same methodology as the DAX performance test.\n");
    return 0;
}