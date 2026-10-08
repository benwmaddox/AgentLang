target triple = "x86_64-pc-windows-msvc"
%NativeExecutionContext = type { i32, i32, i32, i32, i64, i64, ptr, ptr, ptr, i32, i32, ptr, ptr, ptr, i32, i32 }
%NativeExecutionContextAlignmentProbe = type { i8, %NativeExecutionContext }
%NativeTypeDescriptor = type { i32, i32, ptr }
%NativeProgramDescriptor = type { ptr, i32, i32 }
declare i32 @al_runtime_validate_request(ptr, ptr, i32, i32, ptr, i32)
declare i32 @al_runtime_import_state(ptr, ptr, ptr, i32)
declare i32 @al_runtime_make_record(ptr, ptr, i32, ptr, i32, ptr)
declare i32 @al_runtime_get_field(ptr, ptr, i32, i64, i32, ptr)
declare i32 @al_runtime_equal(ptr, ptr, i32, i64, i64, ptr)
declare i32 @al_runtime_promote(ptr, ptr, ptr, ptr, i32, ptr, i32)
declare { i64, i1 } @llvm.sadd.with.overflow.i64(i64, i64)
declare { i64, i1 } @llvm.ssub.with.overflow.i64(i64, i64)
declare { i64, i1 } @llvm.smul.with.overflow.i64(i64, i64)

@agentlang_fields_4 = private constant [2 x i32] [i32 5, i32 0]
@agentlang_fields_5 = private constant [1 x i32] [i32 0]
@agentlang_fields_6 = private constant [2 x i32] [i32 0, i32 4]
@agentlang_type_descriptors = private constant [7 x %NativeTypeDescriptor] [%NativeTypeDescriptor { i32 1, i32 0, ptr null }, %NativeTypeDescriptor { i32 2, i32 0, ptr null }, %NativeTypeDescriptor { i32 3, i32 0, ptr null }, %NativeTypeDescriptor { i32 4, i32 0, ptr null }, %NativeTypeDescriptor { i32 4, i32 2, ptr getelementptr inbounds ([2 x i32], ptr @agentlang_fields_4, i32 0, i32 0) }, %NativeTypeDescriptor { i32 4, i32 1, ptr getelementptr inbounds ([1 x i32], ptr @agentlang_fields_5, i32 0, i32 0) }, %NativeTypeDescriptor { i32 4, i32 2, ptr getelementptr inbounds ([2 x i32], ptr @agentlang_fields_6, i32 0, i32 0) }]
@agentlang_program = private constant %NativeProgramDescriptor { ptr getelementptr inbounds ([7 x %NativeTypeDescriptor], ptr @agentlang_type_descriptors, i32 0, i32 0), i32 7, i32 0 }
@agentlang_root_type_ids = private constant [1 x i32] [i32 4]
@agentlang_input_type_ids = private constant [0 x i32] []

define internal void @agentlang_body(ptr %ctx, ptr %outputs, ptr %status, i32 %depth) {
entry:
  %ctx.field.0 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.1 = load i32, ptr %ctx.field.0, align 4
  %steps.next.2 = add i32 %steps.current.1, 1
  store i32 %steps.next.2, ptr %ctx.field.0, align 4
  %steps.over.3 = icmp ugt i32 %steps.next.2, 10000
  br i1 %steps.over.3, label %failure.4, label %continue.5
failure.4:
  %ctx.field.6 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 0, ptr %ctx.field.6, align 4
  %status.diagnostic.7 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.7, align 4
  ret void
continue.5:
  %ctx.field.8 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.9 = load i32, ptr %ctx.field.8, align 4
  %steps.next.10 = add i32 %steps.current.9, 1
  store i32 %steps.next.10, ptr %ctx.field.8, align 4
  %steps.over.11 = icmp ugt i32 %steps.next.10, 10000
  br i1 %steps.over.11, label %failure.12, label %continue.13
failure.12:
  %ctx.field.14 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 1, ptr %ctx.field.14, align 4
  %status.diagnostic.15 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.15, align 4
  ret void
continue.13:
  %ctx.field.16 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.17 = load i32, ptr %ctx.field.16, align 4
  %steps.next.18 = add i32 %steps.current.17, 1
  store i32 %steps.next.18, ptr %ctx.field.16, align 4
  %steps.over.19 = icmp ugt i32 %steps.next.18, 10000
  br i1 %steps.over.19, label %failure.20, label %continue.21
failure.20:
  %ctx.field.22 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 2, ptr %ctx.field.22, align 4
  %status.diagnostic.23 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.23, align 4
  ret void
continue.21:
  %depth.exceeded.24 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.24, label %failure.25, label %continue.26
failure.25:
  %ctx.field.27 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 3, ptr %ctx.field.27, align 4
  %status.diagnostic.28 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.28, align 4
  ret void
continue.26:
  %call.depth.29 = add i32 %depth, 1
  call void @agentlang_fn_595af45502edb375d2c34ed58568618551d754bf531d9216b84c417c2cf7efb0_r1(ptr %ctx, ptr %outputs, ptr %status, i32 %call.depth.29, i64 17, i64 700)
  %callee.status.30 = load i32, ptr %status, align 4
  %callee.succeeded.31 = icmp eq i32 %callee.status.30, 0
  br i1 %callee.succeeded.31, label %callee.continue.33, label %callee.failure.32
callee.failure.32:
  ret void
callee.continue.33:
  %call.output.ptr.34 = getelementptr inbounds i64, ptr %outputs, i64 0
  %call.output.35 = load i64, ptr %call.output.ptr.34, align 8
  %ctx.field.36 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.37 = load i32, ptr %ctx.field.36, align 4
  %steps.next.38 = add i32 %steps.current.37, 1
  store i32 %steps.next.38, ptr %ctx.field.36, align 4
  %steps.over.39 = icmp ugt i32 %steps.next.38, 10000
  br i1 %steps.over.39, label %failure.40, label %continue.41
failure.40:
  %ctx.field.42 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 4, ptr %ctx.field.42, align 4
  %status.diagnostic.43 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.43, align 4
  ret void
continue.41:
  %ctx.field.44 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.45 = load i32, ptr %ctx.field.44, align 4
  %steps.next.46 = add i32 %steps.current.45, 1
  store i32 %steps.next.46, ptr %ctx.field.44, align 4
  %steps.over.47 = icmp ugt i32 %steps.next.46, 10000
  br i1 %steps.over.47, label %failure.48, label %continue.49
failure.48:
  %ctx.field.50 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 5, ptr %ctx.field.50, align 4
  %status.diagnostic.51 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.51, align 4
  ret void
continue.49:
  %ctx.field.52 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.53 = load i32, ptr %ctx.field.52, align 4
  %steps.next.54 = add i32 %steps.current.53, 1
  store i32 %steps.next.54, ptr %ctx.field.52, align 4
  %steps.over.55 = icmp ugt i32 %steps.next.54, 10000
  br i1 %steps.over.55, label %failure.56, label %continue.57
failure.56:
  %ctx.field.58 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 6, ptr %ctx.field.58, align 4
  %status.diagnostic.59 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.59, align 4
  ret void
continue.57:
  %ctx.field.60 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.61 = load i32, ptr %ctx.field.60, align 4
  %steps.next.62 = add i32 %steps.current.61, 1
  store i32 %steps.next.62, ptr %ctx.field.60, align 4
  %steps.over.63 = icmp ugt i32 %steps.next.62, 10000
  br i1 %steps.over.63, label %failure.64, label %continue.65
failure.64:
  %ctx.field.66 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 7, ptr %ctx.field.66, align 4
  %status.diagnostic.67 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.67, align 4
  ret void
continue.65:
  %depth.exceeded.68 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.68, label %failure.69, label %continue.70
failure.69:
  %ctx.field.71 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 8, ptr %ctx.field.71, align 4
  %status.diagnostic.72 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.72, align 4
  ret void
continue.70:
  %workspace.slot.73 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 99, ptr %workspace.slot.73, align 8
  %workspace.slot.74 = getelementptr inbounds i64, ptr %outputs, i64 1
  %record.make.status.75 = call i32 @al_runtime_make_record(ptr %ctx, ptr @agentlang_program, i32 5, ptr %outputs, i32 1, ptr %workspace.slot.74)
  switch i32 %record.make.status.75, label %runtime.invalid.result.81 [ i32 0, label %runtime.success.76 i32 1, label %runtime.invalid.request.77 i32 2, label %runtime.invalid.reference.78 i32 3, label %runtime.scratch.capacity.79 i32 4, label %runtime.retained.capacity.80 ]
runtime.invalid.request.77:
  store i32 2, ptr %status, align 4
  ret void
runtime.invalid.reference.78:
  store i32 5, ptr %status, align 4
  ret void
runtime.scratch.capacity.79:
  store i32 3, ptr %status, align 4
  ret void
runtime.retained.capacity.80:
  store i32 4, ptr %status, align 4
  ret void
runtime.invalid.result.81:
  store i32 2, ptr %status, align 4
  ret void
runtime.success.76:
  %record.make.handle.82 = load i64, ptr %workspace.slot.74, align 8
  %ctx.field.83 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.84 = load i32, ptr %ctx.field.83, align 4
  %steps.next.85 = add i32 %steps.current.84, 1
  store i32 %steps.next.85, ptr %ctx.field.83, align 4
  %steps.over.86 = icmp ugt i32 %steps.next.85, 10000
  br i1 %steps.over.86, label %failure.87, label %continue.88
failure.87:
  %ctx.field.89 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 9, ptr %ctx.field.89, align 4
  %status.diagnostic.90 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.90, align 4
  ret void
continue.88:
  %ctx.field.91 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.92 = load i32, ptr %ctx.field.91, align 4
  %steps.next.93 = add i32 %steps.current.92, 1
  store i32 %steps.next.93, ptr %ctx.field.91, align 4
  %steps.over.94 = icmp ugt i32 %steps.next.93, 10000
  br i1 %steps.over.94, label %failure.95, label %continue.96
failure.95:
  %ctx.field.97 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 10, ptr %ctx.field.97, align 4
  %status.diagnostic.98 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.98, align 4
  ret void
continue.96:
  %ctx.field.99 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.100 = load i32, ptr %ctx.field.99, align 4
  %steps.next.101 = add i32 %steps.current.100, 1
  store i32 %steps.next.101, ptr %ctx.field.99, align 4
  %steps.over.102 = icmp ugt i32 %steps.next.101, 10000
  br i1 %steps.over.102, label %failure.103, label %continue.104
failure.103:
  %ctx.field.105 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 11, ptr %ctx.field.105, align 4
  %status.diagnostic.106 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.106, align 4
  ret void
continue.104:
  %depth.exceeded.107 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.107, label %failure.108, label %continue.109
failure.108:
  %ctx.field.110 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 12, ptr %ctx.field.110, align 4
  %status.diagnostic.111 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.111, align 4
  ret void
continue.109:
  %workspace.slot.112 = getelementptr inbounds i64, ptr %outputs, i64 0
  %record.field.status.113 = call i32 @al_runtime_get_field(ptr %ctx, ptr @agentlang_program, i32 5, i64 %record.make.handle.82, i32 0, ptr %workspace.slot.112)
  switch i32 %record.field.status.113, label %runtime.invalid.result.119 [ i32 0, label %runtime.success.114 i32 1, label %runtime.invalid.request.115 i32 2, label %runtime.invalid.reference.116 i32 3, label %runtime.scratch.capacity.117 i32 4, label %runtime.retained.capacity.118 ]
runtime.invalid.request.115:
  store i32 2, ptr %status, align 4
  ret void
runtime.invalid.reference.116:
  store i32 5, ptr %status, align 4
  ret void
runtime.scratch.capacity.117:
  store i32 3, ptr %status, align 4
  ret void
runtime.retained.capacity.118:
  store i32 4, ptr %status, align 4
  ret void
runtime.invalid.result.119:
  store i32 2, ptr %status, align 4
  ret void
runtime.success.114:
  %record.field.value.120 = load i64, ptr %workspace.slot.112, align 8
  %ctx.field.121 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.122 = load i32, ptr %ctx.field.121, align 4
  %steps.next.123 = add i32 %steps.current.122, 1
  store i32 %steps.next.123, ptr %ctx.field.121, align 4
  %steps.over.124 = icmp ugt i32 %steps.next.123, 10000
  br i1 %steps.over.124, label %failure.125, label %continue.126
failure.125:
  %ctx.field.127 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 13, ptr %ctx.field.127, align 4
  %status.diagnostic.128 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.128, align 4
  ret void
continue.126:
  %depth.exceeded.129 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.129, label %failure.130, label %continue.131
failure.130:
  %ctx.field.132 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 14, ptr %ctx.field.132, align 4
  %status.diagnostic.133 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.133, align 4
  ret void
continue.131:
  %ctx.field.134 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.135 = load i32, ptr %ctx.field.134, align 4
  %steps.next.136 = add i32 %steps.current.135, 1
  store i32 %steps.next.136, ptr %ctx.field.134, align 4
  %steps.over.137 = icmp ugt i32 %steps.next.136, 10000
  br i1 %steps.over.137, label %failure.138, label %continue.139
failure.138:
  %ctx.field.140 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 15, ptr %ctx.field.140, align 4
  %status.diagnostic.141 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.141, align 4
  ret void
continue.139:
  %output.ptr.142 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %call.output.35, ptr %output.ptr.142, align 8
  ret void
}

define internal void @agentlang_fn_595af45502edb375d2c34ed58568618551d754bf531d9216b84c417c2cf7efb0_r1(ptr %ctx, ptr %outputs, ptr %status, i32 %depth, i64 %arg0, i64 %arg1) {
entry:
  %ctx.field.0 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.1 = load i32, ptr %ctx.field.0, align 4
  %steps.next.2 = add i32 %steps.current.1, 1
  store i32 %steps.next.2, ptr %ctx.field.0, align 4
  %steps.over.3 = icmp ugt i32 %steps.next.2, 10000
  br i1 %steps.over.3, label %failure.4, label %continue.5
failure.4:
  %ctx.field.6 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 16, ptr %ctx.field.6, align 4
  %status.diagnostic.7 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.7, align 4
  ret void
continue.5:
  %ctx.field.8 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.9 = load i32, ptr %ctx.field.8, align 4
  %steps.next.10 = add i32 %steps.current.9, 1
  store i32 %steps.next.10, ptr %ctx.field.8, align 4
  %steps.over.11 = icmp ugt i32 %steps.next.10, 10000
  br i1 %steps.over.11, label %failure.12, label %continue.13
failure.12:
  %ctx.field.14 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 17, ptr %ctx.field.14, align 4
  %status.diagnostic.15 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.15, align 4
  ret void
continue.13:
  %ctx.field.16 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.17 = load i32, ptr %ctx.field.16, align 4
  %steps.next.18 = add i32 %steps.current.17, 1
  store i32 %steps.next.18, ptr %ctx.field.16, align 4
  %steps.over.19 = icmp ugt i32 %steps.next.18, 10000
  br i1 %steps.over.19, label %failure.20, label %continue.21
failure.20:
  %ctx.field.22 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 18, ptr %ctx.field.22, align 4
  %status.diagnostic.23 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.23, align 4
  ret void
continue.21:
  %ctx.field.24 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.25 = load i32, ptr %ctx.field.24, align 4
  %steps.next.26 = add i32 %steps.current.25, 1
  store i32 %steps.next.26, ptr %ctx.field.24, align 4
  %steps.over.27 = icmp ugt i32 %steps.next.26, 10000
  br i1 %steps.over.27, label %failure.28, label %continue.29
failure.28:
  %ctx.field.30 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 19, ptr %ctx.field.30, align 4
  %status.diagnostic.31 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.31, align 4
  ret void
continue.29:
  %ctx.field.32 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.33 = load i32, ptr %ctx.field.32, align 4
  %steps.next.34 = add i32 %steps.current.33, 1
  store i32 %steps.next.34, ptr %ctx.field.32, align 4
  %steps.over.35 = icmp ugt i32 %steps.next.34, 10000
  br i1 %steps.over.35, label %failure.36, label %continue.37
failure.36:
  %ctx.field.38 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 20, ptr %ctx.field.38, align 4
  %status.diagnostic.39 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.39, align 4
  ret void
continue.37:
  %ctx.field.40 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.41 = load i32, ptr %ctx.field.40, align 4
  %steps.next.42 = add i32 %steps.current.41, 1
  store i32 %steps.next.42, ptr %ctx.field.40, align 4
  %steps.over.43 = icmp ugt i32 %steps.next.42, 10000
  br i1 %steps.over.43, label %failure.44, label %continue.45
failure.44:
  %ctx.field.46 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 21, ptr %ctx.field.46, align 4
  %status.diagnostic.47 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.47, align 4
  ret void
continue.45:
  %ctx.field.48 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.49 = load i32, ptr %ctx.field.48, align 4
  %steps.next.50 = add i32 %steps.current.49, 1
  store i32 %steps.next.50, ptr %ctx.field.48, align 4
  %steps.over.51 = icmp ugt i32 %steps.next.50, 10000
  br i1 %steps.over.51, label %failure.52, label %continue.53
failure.52:
  %ctx.field.54 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 22, ptr %ctx.field.54, align 4
  %status.diagnostic.55 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.55, align 4
  ret void
continue.53:
  %depth.exceeded.56 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.56, label %failure.57, label %continue.58
failure.57:
  %ctx.field.59 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 23, ptr %ctx.field.59, align 4
  %status.diagnostic.60 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.60, align 4
  ret void
continue.58:
  %workspace.slot.61 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %arg0, ptr %workspace.slot.61, align 8
  %workspace.slot.62 = getelementptr inbounds i64, ptr %outputs, i64 1
  %record.make.status.63 = call i32 @al_runtime_make_record(ptr %ctx, ptr @agentlang_program, i32 5, ptr %outputs, i32 1, ptr %workspace.slot.62)
  switch i32 %record.make.status.63, label %runtime.invalid.result.69 [ i32 0, label %runtime.success.64 i32 1, label %runtime.invalid.request.65 i32 2, label %runtime.invalid.reference.66 i32 3, label %runtime.scratch.capacity.67 i32 4, label %runtime.retained.capacity.68 ]
runtime.invalid.request.65:
  store i32 2, ptr %status, align 4
  ret void
runtime.invalid.reference.66:
  store i32 5, ptr %status, align 4
  ret void
runtime.scratch.capacity.67:
  store i32 3, ptr %status, align 4
  ret void
runtime.retained.capacity.68:
  store i32 4, ptr %status, align 4
  ret void
runtime.invalid.result.69:
  store i32 2, ptr %status, align 4
  ret void
runtime.success.64:
  %record.make.handle.70 = load i64, ptr %workspace.slot.62, align 8
  %ctx.field.71 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.72 = load i32, ptr %ctx.field.71, align 4
  %steps.next.73 = add i32 %steps.current.72, 1
  store i32 %steps.next.73, ptr %ctx.field.71, align 4
  %steps.over.74 = icmp ugt i32 %steps.next.73, 10000
  br i1 %steps.over.74, label %failure.75, label %continue.76
failure.75:
  %ctx.field.77 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 24, ptr %ctx.field.77, align 4
  %status.diagnostic.78 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.78, align 4
  ret void
continue.76:
  %ctx.field.79 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.80 = load i32, ptr %ctx.field.79, align 4
  %steps.next.81 = add i32 %steps.current.80, 1
  store i32 %steps.next.81, ptr %ctx.field.79, align 4
  %steps.over.82 = icmp ugt i32 %steps.next.81, 10000
  br i1 %steps.over.82, label %failure.83, label %continue.84
failure.83:
  %ctx.field.85 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 25, ptr %ctx.field.85, align 4
  %status.diagnostic.86 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.86, align 4
  ret void
continue.84:
  %ctx.field.87 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.88 = load i32, ptr %ctx.field.87, align 4
  %steps.next.89 = add i32 %steps.current.88, 1
  store i32 %steps.next.89, ptr %ctx.field.87, align 4
  %steps.over.90 = icmp ugt i32 %steps.next.89, 10000
  br i1 %steps.over.90, label %failure.91, label %continue.92
failure.91:
  %ctx.field.93 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 26, ptr %ctx.field.93, align 4
  %status.diagnostic.94 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.94, align 4
  ret void
continue.92:
  %ctx.field.95 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.96 = load i32, ptr %ctx.field.95, align 4
  %steps.next.97 = add i32 %steps.current.96, 1
  store i32 %steps.next.97, ptr %ctx.field.95, align 4
  %steps.over.98 = icmp ugt i32 %steps.next.97, 10000
  br i1 %steps.over.98, label %failure.99, label %continue.100
failure.99:
  %ctx.field.101 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 27, ptr %ctx.field.101, align 4
  %status.diagnostic.102 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.102, align 4
  ret void
continue.100:
  %ctx.field.103 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.104 = load i32, ptr %ctx.field.103, align 4
  %steps.next.105 = add i32 %steps.current.104, 1
  store i32 %steps.next.105, ptr %ctx.field.103, align 4
  %steps.over.106 = icmp ugt i32 %steps.next.105, 10000
  br i1 %steps.over.106, label %failure.107, label %continue.108
failure.107:
  %ctx.field.109 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 28, ptr %ctx.field.109, align 4
  %status.diagnostic.110 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.110, align 4
  ret void
continue.108:
  %ctx.field.111 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.112 = load i32, ptr %ctx.field.111, align 4
  %steps.next.113 = add i32 %steps.current.112, 1
  store i32 %steps.next.113, ptr %ctx.field.111, align 4
  %steps.over.114 = icmp ugt i32 %steps.next.113, 10000
  br i1 %steps.over.114, label %failure.115, label %continue.116
failure.115:
  %ctx.field.117 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 29, ptr %ctx.field.117, align 4
  %status.diagnostic.118 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.118, align 4
  ret void
continue.116:
  %ctx.field.119 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.120 = load i32, ptr %ctx.field.119, align 4
  %steps.next.121 = add i32 %steps.current.120, 1
  store i32 %steps.next.121, ptr %ctx.field.119, align 4
  %steps.over.122 = icmp ugt i32 %steps.next.121, 10000
  br i1 %steps.over.122, label %failure.123, label %continue.124
failure.123:
  %ctx.field.125 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 30, ptr %ctx.field.125, align 4
  %status.diagnostic.126 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.126, align 4
  ret void
continue.124:
  %ctx.field.127 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.128 = load i32, ptr %ctx.field.127, align 4
  %steps.next.129 = add i32 %steps.current.128, 1
  store i32 %steps.next.129, ptr %ctx.field.127, align 4
  %steps.over.130 = icmp ugt i32 %steps.next.129, 10000
  br i1 %steps.over.130, label %failure.131, label %continue.132
failure.131:
  %ctx.field.133 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 31, ptr %ctx.field.133, align 4
  %status.diagnostic.134 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.134, align 4
  ret void
continue.132:
  %ctx.field.135 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.136 = load i32, ptr %ctx.field.135, align 4
  %steps.next.137 = add i32 %steps.current.136, 1
  store i32 %steps.next.137, ptr %ctx.field.135, align 4
  %steps.over.138 = icmp ugt i32 %steps.next.137, 10000
  br i1 %steps.over.138, label %failure.139, label %continue.140
failure.139:
  %ctx.field.141 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 32, ptr %ctx.field.141, align 4
  %status.diagnostic.142 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.142, align 4
  ret void
continue.140:
  %depth.exceeded.143 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.143, label %failure.144, label %continue.145
failure.144:
  %ctx.field.146 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 33, ptr %ctx.field.146, align 4
  %status.diagnostic.147 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.147, align 4
  ret void
continue.145:
  %workspace.slot.148 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %record.make.handle.70, ptr %workspace.slot.148, align 8
  %workspace.slot.149 = getelementptr inbounds i64, ptr %outputs, i64 1
  store i64 %arg1, ptr %workspace.slot.149, align 8
  %workspace.slot.150 = getelementptr inbounds i64, ptr %outputs, i64 2
  %record.make.status.151 = call i32 @al_runtime_make_record(ptr %ctx, ptr @agentlang_program, i32 4, ptr %outputs, i32 2, ptr %workspace.slot.150)
  switch i32 %record.make.status.151, label %runtime.invalid.result.157 [ i32 0, label %runtime.success.152 i32 1, label %runtime.invalid.request.153 i32 2, label %runtime.invalid.reference.154 i32 3, label %runtime.scratch.capacity.155 i32 4, label %runtime.retained.capacity.156 ]
runtime.invalid.request.153:
  store i32 2, ptr %status, align 4
  ret void
runtime.invalid.reference.154:
  store i32 5, ptr %status, align 4
  ret void
runtime.scratch.capacity.155:
  store i32 3, ptr %status, align 4
  ret void
runtime.retained.capacity.156:
  store i32 4, ptr %status, align 4
  ret void
runtime.invalid.result.157:
  store i32 2, ptr %status, align 4
  ret void
runtime.success.152:
  %record.make.handle.158 = load i64, ptr %workspace.slot.150, align 8
  %output.ptr.159 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %record.make.handle.158, ptr %output.ptr.159, align 8
  ret void
}

define dllexport i32 @agentlang_output_capacity() { ret i32 3 }

define dllexport void @agentlang_abi_layout(ptr %output) {
entry:
  %size.ptr = getelementptr %NativeExecutionContext, ptr null, i32 1
  %size = ptrtoint ptr %size.ptr to i64
  %align.ptr = getelementptr %NativeExecutionContextAlignmentProbe, ptr null, i32 0, i32 1
  %alignment = ptrtoint ptr %align.ptr to i64
  %field0.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 0
  %field0 = ptrtoint ptr %field0.ptr to i64
  %field1.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 1
  %field1 = ptrtoint ptr %field1.ptr to i64
  %field2.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 2
  %field2 = ptrtoint ptr %field2.ptr to i64
  %field3.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 3
  %field3 = ptrtoint ptr %field3.ptr to i64
  %field4.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 4
  %field4 = ptrtoint ptr %field4.ptr to i64
  %field5.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 5
  %field5 = ptrtoint ptr %field5.ptr to i64
  %field6.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 6
  %field6 = ptrtoint ptr %field6.ptr to i64
  %field7.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 7
  %field7 = ptrtoint ptr %field7.ptr to i64
  %field8.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 8
  %field8 = ptrtoint ptr %field8.ptr to i64
  %field9.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 9
  %field9 = ptrtoint ptr %field9.ptr to i64
  %field10.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 10
  %field10 = ptrtoint ptr %field10.ptr to i64
  %field11.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 11
  %field11 = ptrtoint ptr %field11.ptr to i64
  %field12.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 12
  %field12 = ptrtoint ptr %field12.ptr to i64
  %field13.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 13
  %field13 = ptrtoint ptr %field13.ptr to i64
  %field14.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 14
  %field14 = ptrtoint ptr %field14.ptr to i64
  %field15.ptr = getelementptr %NativeExecutionContext, ptr null, i32 0, i32 15
  %field15 = ptrtoint ptr %field15.ptr to i64
  %items = alloca [18 x i64], align 8
  %out0 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 0
  store i64 %size, ptr %out0, align 8
  %out1 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 1
  store i64 %alignment, ptr %out1, align 8
  %out2 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 2
  store i64 %field0, ptr %out2, align 8
  %out3 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 3
  store i64 %field1, ptr %out3, align 8
  %out4 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 4
  store i64 %field2, ptr %out4, align 8
  %out5 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 5
  store i64 %field3, ptr %out5, align 8
  %out6 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 6
  store i64 %field4, ptr %out6, align 8
  %out7 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 7
  store i64 %field5, ptr %out7, align 8
  %out8 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 8
  store i64 %field6, ptr %out8, align 8
  %out9 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 9
  store i64 %field7, ptr %out9, align 8
  %out10 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 10
  store i64 %field8, ptr %out10, align 8
  %out11 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 11
  store i64 %field9, ptr %out11, align 8
  %out12 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 12
  store i64 %field10, ptr %out12, align 8
  %out13 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 13
  store i64 %field11, ptr %out13, align 8
  %out14 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 14
  store i64 %field12, ptr %out14, align 8
  %out15 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 15
  store i64 %field13, ptr %out15, align 8
  %out16 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 16
  store i64 %field14, ptr %out16, align 8
  %out17 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 17
  store i64 %field15, ptr %out17, align 8
  %item0 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 0
  %value0 = load i64, ptr %item0, align 8
  %dest0 = getelementptr inbounds i64, ptr %output, i64 0
  store i64 %value0, ptr %dest0, align 8
  %item1 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 1
  %value1 = load i64, ptr %item1, align 8
  %dest1 = getelementptr inbounds i64, ptr %output, i64 1
  store i64 %value1, ptr %dest1, align 8
  %item2 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 2
  %value2 = load i64, ptr %item2, align 8
  %dest2 = getelementptr inbounds i64, ptr %output, i64 2
  store i64 %value2, ptr %dest2, align 8
  %item3 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 3
  %value3 = load i64, ptr %item3, align 8
  %dest3 = getelementptr inbounds i64, ptr %output, i64 3
  store i64 %value3, ptr %dest3, align 8
  %item4 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 4
  %value4 = load i64, ptr %item4, align 8
  %dest4 = getelementptr inbounds i64, ptr %output, i64 4
  store i64 %value4, ptr %dest4, align 8
  %item5 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 5
  %value5 = load i64, ptr %item5, align 8
  %dest5 = getelementptr inbounds i64, ptr %output, i64 5
  store i64 %value5, ptr %dest5, align 8
  %item6 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 6
  %value6 = load i64, ptr %item6, align 8
  %dest6 = getelementptr inbounds i64, ptr %output, i64 6
  store i64 %value6, ptr %dest6, align 8
  %item7 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 7
  %value7 = load i64, ptr %item7, align 8
  %dest7 = getelementptr inbounds i64, ptr %output, i64 7
  store i64 %value7, ptr %dest7, align 8
  %item8 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 8
  %value8 = load i64, ptr %item8, align 8
  %dest8 = getelementptr inbounds i64, ptr %output, i64 8
  store i64 %value8, ptr %dest8, align 8
  %item9 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 9
  %value9 = load i64, ptr %item9, align 8
  %dest9 = getelementptr inbounds i64, ptr %output, i64 9
  store i64 %value9, ptr %dest9, align 8
  %item10 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 10
  %value10 = load i64, ptr %item10, align 8
  %dest10 = getelementptr inbounds i64, ptr %output, i64 10
  store i64 %value10, ptr %dest10, align 8
  %item11 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 11
  %value11 = load i64, ptr %item11, align 8
  %dest11 = getelementptr inbounds i64, ptr %output, i64 11
  store i64 %value11, ptr %dest11, align 8
  %item12 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 12
  %value12 = load i64, ptr %item12, align 8
  %dest12 = getelementptr inbounds i64, ptr %output, i64 12
  store i64 %value12, ptr %dest12, align 8
  %item13 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 13
  %value13 = load i64, ptr %item13, align 8
  %dest13 = getelementptr inbounds i64, ptr %output, i64 13
  store i64 %value13, ptr %dest13, align 8
  %item14 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 14
  %value14 = load i64, ptr %item14, align 8
  %dest14 = getelementptr inbounds i64, ptr %output, i64 14
  store i64 %value14, ptr %dest14, align 8
  %item15 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 15
  %value15 = load i64, ptr %item15, align 8
  %dest15 = getelementptr inbounds i64, ptr %output, i64 15
  store i64 %value15, ptr %dest15, align 8
  %item16 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 16
  %value16 = load i64, ptr %item16, align 8
  %dest16 = getelementptr inbounds i64, ptr %output, i64 16
  store i64 %value16, ptr %dest16, align 8
  %item17 = getelementptr inbounds [18 x i64], ptr %items, i64 0, i64 17
  %value17 = load i64, ptr %item17, align 8
  %dest17 = getelementptr inbounds i64, ptr %output, i64 17
  store i64 %value17, ptr %dest17, align 8
  ret void
}

define dllexport void @agentlang_execute(ptr %ctx, ptr %outputs, i32 %capacity, ptr %status) {
entry:
  %validation.result = call i32 @al_runtime_validate_request(ptr %ctx, ptr %outputs, i32 1, i32 %capacity, ptr %status, i32 3)
  %request.valid = icmp eq i32 %validation.result, 0
  br i1 %request.valid, label %request.accepted, label %request.rejected
request.rejected:
  ret void
request.accepted:
  %steps.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  store i32 0, ptr %steps.ptr, align 4
  %error.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 -1, ptr %error.ptr, align 4
  %reserved.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 3
  store i32 0, ptr %reserved.ptr, align 4
  %arg0.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 4
  store i64 0, ptr %arg0.ptr, align 8
  %arg1.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 5
  store i64 0, ptr %arg1.ptr, align 8
  %workspace.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 8
  %workspace = load ptr, ptr %workspace.ptr, align 8
  %import.result = call i32 @al_runtime_import_state(ptr %ctx, ptr @agentlang_program, ptr null, i32 0)
  switch i32 %import.result, label %import.invalid [ i32 0, label %import.succeeded i32 1, label %import.invalid.request i32 2, label %import.invalid.reference i32 3, label %import.scratch.capacity i32 4, label %import.retained.capacity ]
import.invalid:
  store i32 2, ptr %status, align 4
  ret void
import.invalid.request:
  store i32 2, ptr %status, align 4
  ret void
import.invalid.reference:
  store i32 5, ptr %status, align 4
  ret void
import.scratch.capacity:
  store i32 3, ptr %status, align 4
  ret void
import.retained.capacity:
  store i32 4, ptr %status, align 4
  ret void
import.succeeded:
  call void @agentlang_body(ptr %ctx, ptr %workspace, ptr %status, i32 0)
  %body.status = load i32, ptr %status, align 4
  %body.succeeded = icmp eq i32 %body.status, 0
  br i1 %body.succeeded, label %promote, label %body.failed
body.failed:
  ret void
promote:
  %promote.result = call i32 @al_runtime_promote(ptr %ctx, ptr @agentlang_program, ptr %workspace, ptr @agentlang_root_type_ids, i32 1, ptr %outputs, i32 %capacity)
  switch i32 %promote.result, label %promote.invalid [ i32 0, label %promote.succeeded i32 1, label %promote.invalid.request i32 2, label %promote.invalid.reference i32 3, label %promote.scratch.capacity i32 4, label %promote.retained.capacity ]
promote.invalid:
  store i32 2, ptr %status, align 4
  ret void
promote.invalid.request:
  store i32 2, ptr %status, align 4
  ret void
promote.invalid.reference:
  store i32 5, ptr %status, align 4
  ret void
promote.scratch.capacity:
  store i32 3, ptr %status, align 4
  ret void
promote.retained.capacity:
  store i32 4, ptr %status, align 4
  ret void
promote.succeeded:
  ret void
}
