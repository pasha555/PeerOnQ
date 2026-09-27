# Performance reference

Read only when performance, streaming, concurrency, latency, throughput, memory, or CPU is relevant.

## Workflow
1. Identify the user-visible or system metric.
2. Measure or obtain evidence before optimizing.
3. Locate the dominant bottleneck.
4. Make the smallest change that improves it.
5. Re-measure and check regressions.

Avoid speculative micro-optimizations.

## Common production checks
- bound queues/buffers;
- apply backpressure where producers can outrun consumers;
- avoid unbounded task/thread creation;
- cancel obsolete work;
- batch or coalesce high-frequency updates when correctness allows;
- reuse expensive connections/resources;
- use pagination/streaming for large datasets;
- cache only with clear ownership, invalidation, and memory bounds;
- avoid logging in tight hot loops;
- prevent reconnect storms with exponential backoff + jitter.

For streaming systems, track latency distribution, dropped/late work, reconnects, queue depth, CPU, memory, and bandwidth rather than only average throughput.
