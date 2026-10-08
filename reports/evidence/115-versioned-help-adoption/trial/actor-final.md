Updated and durably committed `invoice.queue-reminder-once` as Flow/2. It returns existing reminder contents without writing, writes `"queued"` only when the path is absent, and returns `"not-open"` without provider calls for other statuses.

The attached tests cover missing, queued, custom, case-mismatched, and repeated-call cases. The repeated-call test verifies two reads per invocation and zero writes to an existing marker. The operation tests passed **5/5** with **20/20 instructions and 4/4 branch outcomes covered**; the unchanged path-helper tests passed **2/2**, and the existing example passed **1/1**.

I sent `host.close`; a subsequent poll returned `Unknown process id`, indicating the broker process had terminated.
