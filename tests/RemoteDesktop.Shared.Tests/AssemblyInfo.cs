// The handshake/relay tests each run Argon2id (64 MiB, memory-hard). Running
// several in parallel starves CPU/memory and makes them flaky under a timeout, so
// the suite runs sequentially. Total runtime stays a few seconds.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
