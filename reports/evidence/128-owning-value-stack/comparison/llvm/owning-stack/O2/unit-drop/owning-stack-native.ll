%AlOwningContext = type { i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, ptr, ptr, ptr, ptr, i64, i64, i64, i64 }
declare void @al_owning_begin(ptr)
declare i32 @al_owning_enter_frame(ptr, i32)
declare void @al_owning_leave_frame(ptr)
declare i32 @al_owning_reserve_to(ptr, i32, i32)
declare void @al_owning_release_to(ptr, i32, i32, i32, i32)
declare i64 @al_owning_load_i64(ptr, i32)
declare void @al_owning_store_i64(ptr, i32, i64, i32)
declare void @al_owning_store_token(ptr, i32, i32)
declare void @al_owning_copy_external(ptr, i32, ptr, i32, i32, i32)
declare void @al_owning_move_range(ptr, i32, i32, i32, i32, i32, i32)
declare void @al_owning_duplicate(ptr, i32, i32, i32, i32, i32)
declare void @al_owning_drop(ptr, i32, i32, i32, i32)
declare void @al_owning_store_local(ptr, i32, i32, i32, i32, i32, i32, i32)
declare void @al_owning_load_local(ptr, i32, i32, i32, i32, i32)
declare void @al_owning_clear_local(ptr, i32, i32, i32, i32)
declare void @al_owning_local_reserve(ptr, i32)
declare void @al_owning_local_release(ptr, i32)
declare void @al_owning_update_live(ptr, i32, i32)
declare void @al_owning_record_layout(ptr, i32, i32, i32, i32, i32, i32, i32)
declare void @al_owning_swap(ptr, i32, i32, i32, i32, i32)
declare i32 @al_owning_equal(ptr, i32, i32, i32)
declare void @al_owning_publish(ptr, ptr, i32, i32, i32, i32)
declare i32 @al_owning_check_cursor(ptr, i32)
declare i32 @al_owning_charge_step(ptr, i32)
declare void @al_owning_set_failure(ptr, i32, i32, i32, i32)
declare { i64, i1 } @llvm.sadd.with.overflow.i64(i64, i64)
declare { i64, i1 } @llvm.ssub.with.overflow.i64(i64, i64)
declare { i64, i1 } @llvm.smul.with.overflow.i64(i64, i64)

define internal i32 @agentlang_entry_frame(ptr %ctx, i32 %argument.source, i32 %result.destination, i32 %depth.error) {
entry:
  %context.field.3 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 2
  %context.value.4 = load i32, ptr %context.field.3, align 4
  %context.field.5 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.6 = load i32, ptr %context.field.5, align 4
  %context.field.7 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.8 = load i32, ptr %context.field.7, align 4
  %context.field.9 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.10 = load i32, ptr %context.field.9, align 4
  %frame.entered.11 = call i32 @al_owning_enter_frame(ptr %ctx, i32 %depth.error)
  %frame.entered.ok.12 = icmp eq i32 %frame.entered.11, 0
  br i1 %frame.entered.ok.12, label %frame.entered.continue.13, label %frame.enter.failure.2
frame.enter.failure.2:
  ret i32 1
frame.entered.continue.13:
  %context.field.14 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 2
  %context.value.15 = load i32, ptr %context.field.14, align 4
  %reserve.status.16 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %context.value.15, i32 1)
  %reserve.ok.17 = icmp eq i32 %reserve.status.16, 0
  br i1 %reserve.ok.17, label %reserve.continue.18, label %frame.reserve.failure.1
reserve.continue.18:
  call void @al_owning_local_reserve(ptr %ctx, i32 0)
  %context.field.19 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.20 = load i32, ptr %context.field.19, align 4
  %runtime.ok.21 = icmp eq i32 %context.value.20, 0
  br i1 %runtime.ok.21, label %runtime.continue.22, label %frame.failure.0
runtime.continue.22:
  %step.status.23 = call i32 @al_owning_charge_step(ptr %ctx, i32 2)
  %step.ok.24 = icmp eq i32 %step.status.23, 0
  br i1 %step.ok.24, label %step.continue.25, label %frame.failure.0
step.continue.25:
  %stack.offset.26 = add i32 %context.value.15, 8
  %reserve.status.27 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.26, i32 3)
  %reserve.ok.28 = icmp eq i32 %reserve.status.27, 0
  br i1 %reserve.ok.28, label %reserve.continue.29, label %frame.failure.0
reserve.continue.29:
  call void @al_owning_store_i64(ptr %ctx, i32 %context.value.15, i64 0, i32 3)
  %context.field.30 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.31 = load i32, ptr %context.field.30, align 4
  %runtime.ok.32 = icmp eq i32 %context.value.31, 0
  br i1 %runtime.ok.32, label %runtime.continue.33, label %frame.failure.0
runtime.continue.33:
  call void @al_owning_update_live(ptr %ctx, i32 8, i32 0)
  %context.field.34 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.35 = load i32, ptr %context.field.34, align 4
  %runtime.ok.36 = icmp eq i32 %context.value.35, 0
  br i1 %runtime.ok.36, label %runtime.continue.37, label %frame.failure.0
runtime.continue.37:
  %stack.offset.38 = add i32 %context.value.15, 8
  %cursor.check.39 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.38)
  %cursor.ok.40 = icmp eq i32 %cursor.check.39, 0
  br i1 %cursor.ok.40, label %cursor.continue.41, label %frame.failure.0
cursor.continue.41:
  %step.status.42 = call i32 @al_owning_charge_step(ptr %ctx, i32 4)
  %step.ok.43 = icmp eq i32 %step.status.42, 0
  br i1 %step.ok.43, label %step.continue.44, label %frame.failure.0
step.continue.44:
  call void @al_owning_drop(ptr %ctx, i32 %context.value.15, i32 8, i32 8, i32 3)
  %context.field.45 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.46 = load i32, ptr %context.field.45, align 4
  %runtime.ok.47 = icmp eq i32 %context.value.46, 0
  br i1 %runtime.ok.47, label %runtime.continue.48, label %frame.failure.0
runtime.continue.48:
  %cursor.check.49 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %context.value.15)
  %cursor.ok.50 = icmp eq i32 %cursor.check.49, 0
  br i1 %cursor.ok.50, label %cursor.continue.51, label %frame.failure.0
cursor.continue.51:
  %step.status.52 = call i32 @al_owning_charge_step(ptr %ctx, i32 5)
  %step.ok.53 = icmp eq i32 %step.status.52, 0
  br i1 %step.ok.53, label %step.continue.54, label %frame.failure.0
step.continue.54:
  %stack.offset.55 = add i32 %context.value.15, 8
  %reserve.status.56 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.55, i32 6)
  %reserve.ok.57 = icmp eq i32 %reserve.status.56, 0
  br i1 %reserve.ok.57, label %reserve.continue.58, label %frame.failure.0
reserve.continue.58:
  call void @al_owning_store_i64(ptr %ctx, i32 %context.value.15, i64 123, i32 1)
  %context.field.59 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.60 = load i32, ptr %context.field.59, align 4
  %runtime.ok.61 = icmp eq i32 %context.value.60, 0
  br i1 %runtime.ok.61, label %runtime.continue.62, label %frame.failure.0
runtime.continue.62:
  call void @al_owning_update_live(ptr %ctx, i32 8, i32 0)
  %context.field.63 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.64 = load i32, ptr %context.field.63, align 4
  %runtime.ok.65 = icmp eq i32 %context.value.64, 0
  br i1 %runtime.ok.65, label %runtime.continue.66, label %frame.failure.0
runtime.continue.66:
  %stack.offset.67 = add i32 %context.value.15, 8
  %cursor.check.68 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.67)
  %cursor.ok.69 = icmp eq i32 %cursor.check.68, 0
  br i1 %cursor.ok.69, label %cursor.continue.70, label %frame.failure.0
cursor.continue.70:
  call void @al_owning_move_range(ptr %ctx, i32 %result.destination, i32 %context.value.15, i32 8, i32 8, i32 1, i32 9)
  %context.field.71 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.72 = load i32, ptr %context.field.71, align 4
  %runtime.ok.73 = icmp eq i32 %context.value.72, 0
  br i1 %runtime.ok.73, label %runtime.continue.74, label %frame.failure.0
runtime.continue.74:
  call void @al_owning_local_release(ptr %ctx, i32 0)
  call void @al_owning_release_to(ptr %ctx, i32 %context.value.15, i32 0, i32 0, i32 0)
  call void @al_owning_leave_frame(ptr %ctx)
  ret i32 0
frame.reserve.failure.1:
  call void @al_owning_release_to(ptr %ctx, i32 %context.value.4, i32 0, i32 0, i32 0)
  call void @al_owning_leave_frame(ptr %ctx)
  ret i32 1
frame.failure.0:
  call void @al_owning_release_to(ptr %ctx, i32 %context.value.4, i32 0, i32 0, i32 0)
  %context.field.75 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.76 = load i32, ptr %context.field.75, align 4
  %context.field.77 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.78 = load i32, ptr %context.field.77, align 4
  %context.field.79 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.80 = load i32, ptr %context.field.79, align 4
  %baseline.live.total.81 = add i32 %context.value.6, %context.value.8
  %current.live.total.82 = add i32 %context.value.76, %context.value.78
  %cleanup.live.delta.83 = sub i32 %baseline.live.total.81, %current.live.total.82
  %cleanup.local.delta.84 = sub i32 %context.value.8, %context.value.78
  %cleanup.reserved.delta.85 = sub i32 %context.value.80, %context.value.10
  call void @al_owning_update_live(ptr %ctx, i32 %cleanup.live.delta.83, i32 %cleanup.local.delta.84)
  call void @al_owning_local_release(ptr %ctx, i32 %cleanup.reserved.delta.85)
  call void @al_owning_leave_frame(ptr %ctx)
  ret i32 1
}


define dllexport i32 @agentlang_owning_execute(ptr %ctx, ptr %input, i32 %input.bytes, ptr %retained, i32 %retained.capacity) {
entry:
  call void @al_owning_begin(ptr %ctx)
  %context.field.3 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.4 = load i32, ptr %context.field.3, align 4
  %entry.context.ok.5 = icmp eq i32 %context.value.4, 0
  br i1 %entry.context.ok.5, label %entry.begin.ok.6, label %entry.failure.0
entry.begin.ok.6:
  %entry.input.length.ok.7 = icmp eq i32 %input.bytes, 0
  br i1 %entry.input.length.ok.7, label %entry.input.valid.8, label %entry.input.invalid.9
entry.input.invalid.9:
  call void @al_owning_set_failure(ptr %ctx, i32 4, i32 0, i32 0, i32 %input.bytes)
  br label %entry.failure.0
entry.input.valid.8:
  %reserve.status.10 = call i32 @al_owning_reserve_to(ptr %ctx, i32 8, i32 7)
  %reserve.ok.11 = icmp eq i32 %reserve.status.10, 0
  br i1 %reserve.ok.11, label %reserve.continue.12, label %entry.failure.0
reserve.continue.12:
  %entry.body.status.13 = call i32 @agentlang_entry_frame(ptr %ctx, i32 0, i32 0, i32 0)
  %entry.body.ok.14 = icmp eq i32 %entry.body.status.13, 0
  br i1 %entry.body.ok.14, label %entry.success.2, label %entry.body.failure.1
entry.body.failure.1:
  %context.field.15 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 2
  %context.value.16 = load i32, ptr %context.field.15, align 4
  %context.field.17 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.18 = load i32, ptr %context.field.17, align 4
  %context.field.19 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.20 = load i32, ptr %context.field.19, align 4
  %context.field.21 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.22 = load i32, ptr %context.field.21, align 4
  call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)
  %entry.failure.live.23 = add i32 %context.value.18, %context.value.20
  %entry.failure.delta.24 = sub i32 0, %entry.failure.live.23
  %entry.failure.local.delta.25 = sub i32 0, %context.value.20
  call void @al_owning_update_live(ptr %ctx, i32 %entry.failure.delta.24, i32 %entry.failure.local.delta.25)
  call void @al_owning_local_release(ptr %ctx, i32 %context.value.22)
  br label %entry.return.status
entry.success.2:
  call void @al_owning_publish(ptr %ctx, ptr %retained, i32 %retained.capacity, i32 0, i32 8, i32 1)
  %context.field.26 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.27 = load i32, ptr %context.field.26, align 4
  %runtime.ok.28 = icmp eq i32 %context.value.27, 0
  br i1 %runtime.ok.28, label %runtime.continue.29, label %entry.failure.0
runtime.continue.29:
  call void @al_owning_update_live(ptr %ctx, i32 -8, i32 0)
  %context.field.30 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.31 = load i32, ptr %context.field.30, align 4
  %runtime.ok.32 = icmp eq i32 %context.value.31, 0
  br i1 %runtime.ok.32, label %runtime.continue.33, label %entry.failure.0
runtime.continue.33:
  call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)
  br label %entry.return.status
entry.failure.0:
  %context.field.34 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 2
  %context.value.35 = load i32, ptr %context.field.34, align 4
  %context.field.36 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.37 = load i32, ptr %context.field.36, align 4
  %context.field.38 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.39 = load i32, ptr %context.field.38, align 4
  %context.field.40 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.41 = load i32, ptr %context.field.40, align 4
  %entry.cleanup.total.live.42 = add i32 %context.value.37, %context.value.39
  %entry.cleanup.live.delta.43 = sub i32 0, %entry.cleanup.total.live.42
  %entry.cleanup.local.delta.44 = sub i32 0, %context.value.39
  call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)
  call void @al_owning_update_live(ptr %ctx, i32 %entry.cleanup.live.delta.43, i32 %entry.cleanup.local.delta.44)
  call void @al_owning_local_release(ptr %ctx, i32 %context.value.41)
  br label %entry.return.status
entry.return.status:
  %context.field.45 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.46 = load i32, ptr %context.field.45, align 4
  ret i32 %context.value.46
}

