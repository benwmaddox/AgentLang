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
@agentlang_root_type_ids = private constant [1 x i32] [i32 0]
@agentlang_input_type_ids = private constant [2 x i32] [i32 6, i32 0]

define internal void @agentlang_body(ptr %ctx, ptr %outputs, ptr %status, i32 %depth, i64 %arg0, i64 %arg1) {
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
  %depth.exceeded.8 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.8, label %failure.9, label %continue.10
failure.9:
  %ctx.field.11 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 1, ptr %ctx.field.11, align 4
  %status.diagnostic.12 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.12, align 4
  ret void
continue.10:
  %call.depth.13 = add i32 %depth, 1
  call void @agentlang_fn_94e7e635bed2c9089731d4c127fb2f1596339d2eaa2f985bd7ac9186bc4f2167_r1(ptr %ctx, ptr %outputs, ptr %status, i32 %call.depth.13, i64 %arg0, i64 %arg1)
  %callee.status.14 = load i32, ptr %status, align 4
  %callee.succeeded.15 = icmp eq i32 %callee.status.14, 0
  br i1 %callee.succeeded.15, label %callee.continue.17, label %callee.failure.16
callee.failure.16:
  ret void
callee.continue.17:
  %call.output.ptr.18 = getelementptr inbounds i64, ptr %outputs, i64 0
  %call.output.19 = load i64, ptr %call.output.ptr.18, align 8
  %output.ptr.20 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %call.output.19, ptr %output.ptr.20, align 8
  ret void
}

define internal void @agentlang_fn_94e7e635bed2c9089731d4c127fb2f1596339d2eaa2f985bd7ac9186bc4f2167_r1(ptr %ctx, ptr %outputs, ptr %status, i32 %depth, i64 %arg0, i64 %arg1) {
entry:
  %ctx.field.0 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.1 = load i32, ptr %ctx.field.0, align 4
  %steps.next.2 = add i32 %steps.current.1, 1
  store i32 %steps.next.2, ptr %ctx.field.0, align 4
  %steps.over.3 = icmp ugt i32 %steps.next.2, 10000
  br i1 %steps.over.3, label %failure.4, label %continue.5
failure.4:
  %ctx.field.6 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 2, ptr %ctx.field.6, align 4
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
  store i32 3, ptr %ctx.field.14, align 4
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
  store i32 4, ptr %ctx.field.22, align 4
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
  store i32 5, ptr %ctx.field.30, align 4
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
  store i32 6, ptr %ctx.field.38, align 4
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
  store i32 7, ptr %ctx.field.46, align 4
  %status.diagnostic.47 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.47, align 4
  ret void
continue.45:
  %depth.exceeded.48 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.48, label %failure.49, label %continue.50
failure.49:
  %ctx.field.51 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 8, ptr %ctx.field.51, align 4
  %status.diagnostic.52 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.52, align 4
  ret void
continue.50:
  %checked.tuple.53 = call { i64, i1 } @llvm.sadd.with.overflow.i64(i64 %arg1, i64 1)
  %checked.raw.54 = extractvalue { i64, i1 } %checked.tuple.53, 0
  %checked.overflow.55 = extractvalue { i64, i1 } %checked.tuple.53, 1
  br i1 %checked.overflow.55, label %overflow.56, label %checked.57
overflow.56:
  %ctx.field.58 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 4
  %ctx.field.59 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 5
  store i64 %arg1, ptr %ctx.field.58, align 8
  store i64 1, ptr %ctx.field.59, align 8
  %ctx.field.60 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 9, ptr %ctx.field.60, align 4
  %status.diagnostic.61 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.61, align 4
  ret void
checked.57:
  %ctx.field.62 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.63 = load i32, ptr %ctx.field.62, align 4
  %steps.next.64 = add i32 %steps.current.63, 1
  store i32 %steps.next.64, ptr %ctx.field.62, align 4
  %steps.over.65 = icmp ugt i32 %steps.next.64, 10000
  br i1 %steps.over.65, label %failure.66, label %continue.67
failure.66:
  %ctx.field.68 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 10, ptr %ctx.field.68, align 4
  %status.diagnostic.69 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.69, align 4
  ret void
continue.67:
  %depth.exceeded.70 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.70, label %failure.71, label %continue.72
failure.71:
  %ctx.field.73 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 11, ptr %ctx.field.73, align 4
  %status.diagnostic.74 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.74, align 4
  ret void
continue.72:
  %call.depth.75 = add i32 %depth, 1
  call void @agentlang_fn_595af45502edb375d2c34ed58568618551d754bf531d9216b84c417c2cf7efb0_r1(ptr %ctx, ptr %outputs, ptr %status, i32 %call.depth.75, i64 %arg1, i64 %checked.raw.54)
  %callee.status.76 = load i32, ptr %status, align 4
  %callee.succeeded.77 = icmp eq i32 %callee.status.76, 0
  br i1 %callee.succeeded.77, label %callee.continue.79, label %callee.failure.78
callee.failure.78:
  ret void
callee.continue.79:
  %call.output.ptr.80 = getelementptr inbounds i64, ptr %outputs, i64 0
  %call.output.81 = load i64, ptr %call.output.ptr.80, align 8
  %ctx.field.82 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.83 = load i32, ptr %ctx.field.82, align 4
  %steps.next.84 = add i32 %steps.current.83, 1
  store i32 %steps.next.84, ptr %ctx.field.82, align 4
  %steps.over.85 = icmp ugt i32 %steps.next.84, 10000
  br i1 %steps.over.85, label %failure.86, label %continue.87
failure.86:
  %ctx.field.88 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 12, ptr %ctx.field.88, align 4
  %status.diagnostic.89 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.89, align 4
  ret void
continue.87:
  %ctx.field.90 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.91 = load i32, ptr %ctx.field.90, align 4
  %steps.next.92 = add i32 %steps.current.91, 1
  store i32 %steps.next.92, ptr %ctx.field.90, align 4
  %steps.over.93 = icmp ugt i32 %steps.next.92, 10000
  br i1 %steps.over.93, label %failure.94, label %continue.95
failure.94:
  %ctx.field.96 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 13, ptr %ctx.field.96, align 4
  %status.diagnostic.97 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.97, align 4
  ret void
continue.95:
  %ctx.field.98 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.99 = load i32, ptr %ctx.field.98, align 4
  %steps.next.100 = add i32 %steps.current.99, 1
  store i32 %steps.next.100, ptr %ctx.field.98, align 4
  %steps.over.101 = icmp ugt i32 %steps.next.100, 10000
  br i1 %steps.over.101, label %failure.102, label %continue.103
failure.102:
  %ctx.field.104 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 14, ptr %ctx.field.104, align 4
  %status.diagnostic.105 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.105, align 4
  ret void
continue.103:
  %ctx.field.106 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.107 = load i32, ptr %ctx.field.106, align 4
  %steps.next.108 = add i32 %steps.current.107, 1
  store i32 %steps.next.108, ptr %ctx.field.106, align 4
  %steps.over.109 = icmp ugt i32 %steps.next.108, 10000
  br i1 %steps.over.109, label %failure.110, label %continue.111
failure.110:
  %ctx.field.112 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 15, ptr %ctx.field.112, align 4
  %status.diagnostic.113 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.113, align 4
  ret void
continue.111:
  %depth.exceeded.114 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.114, label %failure.115, label %continue.116
failure.115:
  %ctx.field.117 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 16, ptr %ctx.field.117, align 4
  %status.diagnostic.118 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.118, align 4
  ret void
continue.116:
  %divide.zero.119 = icmp eq i64 0, 0
  br i1 %divide.zero.119, label %failure.120, label %continue.121
failure.120:
  %ctx.field.122 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 17, ptr %ctx.field.122, align 4
  %status.diagnostic.123 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.123, align 4
  ret void
continue.121:
  %divide.min.124 = icmp eq i64 1, -9223372036854775808
  %divide.negative.one.125 = icmp eq i64 0, -1
  %divide.overflow.126 = and i1 %divide.min.124, %divide.negative.one.125
  br i1 %divide.overflow.126, label %failure.127, label %continue.128
failure.127:
  %ctx.field.129 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 18, ptr %ctx.field.129, align 4
  %status.diagnostic.130 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.130, align 4
  ret void
continue.128:
  %divide.result.131 = sdiv i64 1, 0
  %output.ptr.132 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %divide.result.131, ptr %output.ptr.132, align 8
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
  store i32 19, ptr %ctx.field.6, align 4
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
  store i32 20, ptr %ctx.field.14, align 4
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
  store i32 21, ptr %ctx.field.22, align 4
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
  store i32 22, ptr %ctx.field.30, align 4
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
  store i32 23, ptr %ctx.field.38, align 4
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
  store i32 24, ptr %ctx.field.46, align 4
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
  store i32 25, ptr %ctx.field.54, align 4
  %status.diagnostic.55 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.55, align 4
  ret void
continue.53:
  %depth.exceeded.56 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.56, label %failure.57, label %continue.58
failure.57:
  %ctx.field.59 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 26, ptr %ctx.field.59, align 4
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
  store i32 27, ptr %ctx.field.77, align 4
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
  store i32 28, ptr %ctx.field.85, align 4
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
  store i32 29, ptr %ctx.field.93, align 4
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
  store i32 30, ptr %ctx.field.101, align 4
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
  store i32 31, ptr %ctx.field.109, align 4
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
  store i32 32, ptr %ctx.field.117, align 4
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
  store i32 33, ptr %ctx.field.125, align 4
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
  store i32 34, ptr %ctx.field.133, align 4
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
  store i32 35, ptr %ctx.field.141, align 4
  %status.diagnostic.142 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.142, align 4
  ret void
continue.140:
  %depth.exceeded.143 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.143, label %failure.144, label %continue.145
failure.144:
  %ctx.field.146 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 36, ptr %ctx.field.146, align 4
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
  %import.result = call i32 @al_runtime_import_state(ptr %ctx, ptr @agentlang_program, ptr @agentlang_input_type_ids, i32 2)
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
  %entry.arg0.ptr = getelementptr inbounds i64, ptr %workspace, i64 0
  %entry.arg0 = load i64, ptr %entry.arg0.ptr, align 8
  %entry.arg1.ptr = getelementptr inbounds i64, ptr %workspace, i64 1
  %entry.arg1 = load i64, ptr %entry.arg1.ptr, align 8
  call void @agentlang_body(ptr %ctx, ptr %workspace, ptr %status, i32 0, i64 %entry.arg0, i64 %entry.arg1)
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
