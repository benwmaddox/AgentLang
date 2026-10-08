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

@agentlang_fields_3 = private constant [1 x i32] [i32 0]
@agentlang_fields_4 = private constant [1 x i32] [i32 0]
@agentlang_type_descriptors = private constant [5 x %NativeTypeDescriptor] [%NativeTypeDescriptor { i32 1, i32 0, ptr null }, %NativeTypeDescriptor { i32 2, i32 0, ptr null }, %NativeTypeDescriptor { i32 3, i32 0, ptr null }, %NativeTypeDescriptor { i32 4, i32 1, ptr getelementptr inbounds ([1 x i32], ptr @agentlang_fields_3, i32 0, i32 0) }, %NativeTypeDescriptor { i32 4, i32 1, ptr getelementptr inbounds ([1 x i32], ptr @agentlang_fields_4, i32 0, i32 0) }]
@agentlang_program = private constant %NativeProgramDescriptor { ptr getelementptr inbounds ([5 x %NativeTypeDescriptor], ptr @agentlang_type_descriptors, i32 0, i32 0), i32 5, i32 0 }
@agentlang_root_type_ids = private constant [2 x i32] [i32 4, i32 3]
@agentlang_input_type_ids = private constant [2 x i32] [i32 4, i32 0]

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
  call void @agentlang_fn_8b39cc76e46f8cb831f4d05210d899fca8dfd5e803387118a4d2f5e0009a8b70_r1(ptr %ctx, ptr %outputs, ptr %status, i32 %call.depth.13, i64 %arg0, i64 %arg1)
  %callee.status.14 = load i32, ptr %status, align 4
  %callee.succeeded.15 = icmp eq i32 %callee.status.14, 0
  br i1 %callee.succeeded.15, label %callee.continue.17, label %callee.failure.16
callee.failure.16:
  ret void
callee.continue.17:
  %call.output.ptr.18 = getelementptr inbounds i64, ptr %outputs, i64 0
  %call.output.19 = load i64, ptr %call.output.ptr.18, align 8
  %call.output.ptr.20 = getelementptr inbounds i64, ptr %outputs, i64 1
  %call.output.21 = load i64, ptr %call.output.ptr.20, align 8
  %output.ptr.22 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %call.output.19, ptr %output.ptr.22, align 8
  %output.ptr.23 = getelementptr inbounds i64, ptr %outputs, i64 1
  store i64 %call.output.21, ptr %output.ptr.23, align 8
  ret void
}

define internal void @agentlang_fn_8b39cc76e46f8cb831f4d05210d899fca8dfd5e803387118a4d2f5e0009a8b70_r1(ptr %ctx, ptr %outputs, ptr %status, i32 %depth, i64 %arg0, i64 %arg1) {
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
  %ctx.field.48 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1
  %steps.current.49 = load i32, ptr %ctx.field.48, align 4
  %steps.next.50 = add i32 %steps.current.49, 1
  store i32 %steps.next.50, ptr %ctx.field.48, align 4
  %steps.over.51 = icmp ugt i32 %steps.next.50, 10000
  br i1 %steps.over.51, label %failure.52, label %continue.53
failure.52:
  %ctx.field.54 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 8, ptr %ctx.field.54, align 4
  %status.diagnostic.55 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.55, align 4
  ret void
continue.53:
  %depth.exceeded.56 = icmp ugt i32 %depth, 64
  br i1 %depth.exceeded.56, label %failure.57, label %continue.58
failure.57:
  %ctx.field.59 = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2
  store i32 9, ptr %ctx.field.59, align 4
  %status.diagnostic.60 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.60, align 4
  ret void
continue.58:
  %workspace.slot.61 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %arg1, ptr %workspace.slot.61, align 8
  %workspace.slot.62 = getelementptr inbounds i64, ptr %outputs, i64 1
  %record.make.status.63 = call i32 @al_runtime_make_record(ptr %ctx, ptr @agentlang_program, i32 3, ptr %outputs, i32 1, ptr %workspace.slot.62)
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
  store i32 10, ptr %ctx.field.77, align 4
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
  store i32 11, ptr %ctx.field.85, align 4
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
  store i32 12, ptr %ctx.field.93, align 4
  %status.diagnostic.94 = getelementptr inbounds i32, ptr %status, i64 0
  store i32 1, ptr %status.diagnostic.94, align 4
  ret void
continue.92:
  %output.ptr.95 = getelementptr inbounds i64, ptr %outputs, i64 0
  store i64 %arg0, ptr %output.ptr.95, align 8
  %output.ptr.96 = getelementptr inbounds i64, ptr %outputs, i64 1
  store i64 %record.make.handle.70, ptr %output.ptr.96, align 8
  ret void
}

define dllexport i32 @agentlang_entry_000_output_capacity() { ret i32 2 }

define dllexport void @agentlang_entry_000_abi_layout(ptr %output) {
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

define dllexport void @agentlang_entry_000_execute(ptr %ctx, ptr %outputs, i32 %capacity, ptr %status) {
entry:
  %validation.result = call i32 @al_runtime_validate_request(ptr %ctx, ptr %outputs, i32 2, i32 %capacity, ptr %status, i32 2)
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
  %promote.result = call i32 @al_runtime_promote(ptr %ctx, ptr @agentlang_program, ptr %workspace, ptr @agentlang_root_type_ids, i32 2, ptr %outputs, i32 %capacity)
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
