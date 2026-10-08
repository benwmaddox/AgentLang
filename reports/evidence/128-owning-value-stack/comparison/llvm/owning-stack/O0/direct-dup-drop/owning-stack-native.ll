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
  %stack.offset.16 = add i32 %context.value.15, 16
  %reserve.status.17 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.16, i32 1)
  %reserve.ok.18 = icmp eq i32 %reserve.status.17, 0
  br i1 %reserve.ok.18, label %reserve.continue.19, label %frame.reserve.failure.1
reserve.continue.19:
  call void @al_owning_local_reserve(ptr %ctx, i32 0)
  %context.field.20 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.21 = load i32, ptr %context.field.20, align 4
  %runtime.ok.22 = icmp eq i32 %context.value.21, 0
  br i1 %runtime.ok.22, label %runtime.continue.23, label %frame.failure.0
runtime.continue.23:
  %stack.offset.24 = add i32 %argument.source, 8
  %stack.offset.25 = add i32 %context.value.15, 8
  call void @al_owning_move_range(ptr %ctx, i32 %context.value.15, i32 %argument.source, i32 8, i32 8, i32 1, i32 8)
  %context.field.26 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.27 = load i32, ptr %context.field.26, align 4
  %runtime.ok.28 = icmp eq i32 %context.value.27, 0
  br i1 %runtime.ok.28, label %runtime.continue.29, label %frame.failure.0
runtime.continue.29:
  call void @al_owning_move_range(ptr %ctx, i32 %stack.offset.25, i32 %stack.offset.24, i32 8, i32 8, i32 1, i32 8)
  %context.field.30 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.31 = load i32, ptr %context.field.30, align 4
  %runtime.ok.32 = icmp eq i32 %context.value.31, 0
  br i1 %runtime.ok.32, label %runtime.continue.33, label %frame.failure.0
runtime.continue.33:
  %step.status.34 = call i32 @al_owning_charge_step(ptr %ctx, i32 2)
  %step.ok.35 = icmp eq i32 %step.status.34, 0
  br i1 %step.ok.35, label %step.continue.36, label %frame.failure.0
step.continue.36:
  %stack.offset.37 = add i32 %context.value.15, 16
  %reserve.status.38 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.37, i32 3)
  %reserve.ok.39 = icmp eq i32 %reserve.status.38, 0
  br i1 %reserve.ok.39, label %reserve.continue.40, label %frame.failure.0
reserve.continue.40:
  %callee.status.41 = call i32 @agentlang_word_0000(ptr %ctx, i32 %context.value.15, i32 %context.value.15, i32 4)
  %callee.ok.42 = icmp eq i32 %callee.status.41, 0
  br i1 %callee.ok.42, label %callee.returned.43, label %frame.failure.0
callee.returned.43:
  %stack.offset.44 = add i32 %context.value.15, 16
  call void @al_owning_release_to(ptr %ctx, i32 %stack.offset.44, i32 0, i32 0, i32 0)
  %context.field.45 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.46 = load i32, ptr %context.field.45, align 4
  %runtime.ok.47 = icmp eq i32 %context.value.46, 0
  br i1 %runtime.ok.47, label %runtime.continue.48, label %frame.failure.0
runtime.continue.48:
  %stack.offset.49 = add i32 %context.value.15, 16
  %cursor.check.50 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.49)
  %cursor.ok.51 = icmp eq i32 %cursor.check.50, 0
  br i1 %cursor.ok.51, label %cursor.continue.52, label %frame.failure.0
cursor.continue.52:
  %step.status.53 = call i32 @al_owning_charge_step(ptr %ctx, i32 5)
  %step.ok.54 = icmp eq i32 %step.status.53, 0
  br i1 %step.ok.54, label %step.continue.55, label %frame.failure.0
step.continue.55:
  %stack.offset.56 = add i32 %context.value.15, 16
  %stack.offset.57 = add i32 %stack.offset.56, 16
  %reserve.status.58 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.57, i32 6)
  %reserve.ok.59 = icmp eq i32 %reserve.status.58, 0
  br i1 %reserve.ok.59, label %reserve.continue.60, label %frame.failure.0
reserve.continue.60:
  call void @al_owning_duplicate(ptr %ctx, i32 %stack.offset.56, i32 %context.value.15, i32 16, i32 16, i32 5)
  %context.field.61 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.62 = load i32, ptr %context.field.61, align 4
  %runtime.ok.63 = icmp eq i32 %context.value.62, 0
  br i1 %runtime.ok.63, label %runtime.continue.64, label %frame.failure.0
runtime.continue.64:
  %stack.offset.65 = add i32 %context.value.15, 32
  %cursor.check.66 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.65)
  %cursor.ok.67 = icmp eq i32 %cursor.check.66, 0
  br i1 %cursor.ok.67, label %cursor.continue.68, label %frame.failure.0
cursor.continue.68:
  %step.status.69 = call i32 @al_owning_charge_step(ptr %ctx, i32 7)
  %step.ok.70 = icmp eq i32 %step.status.69, 0
  br i1 %step.ok.70, label %step.continue.71, label %frame.failure.0
step.continue.71:
  %stack.offset.72 = add i32 %context.value.15, 16
  call void @al_owning_drop(ptr %ctx, i32 %stack.offset.72, i32 16, i32 16, i32 5)
  %context.field.73 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.74 = load i32, ptr %context.field.73, align 4
  %runtime.ok.75 = icmp eq i32 %context.value.74, 0
  br i1 %runtime.ok.75, label %runtime.continue.76, label %frame.failure.0
runtime.continue.76:
  %stack.offset.77 = add i32 %context.value.15, 16
  %cursor.check.78 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.77)
  %cursor.ok.79 = icmp eq i32 %cursor.check.78, 0
  br i1 %cursor.ok.79, label %cursor.continue.80, label %frame.failure.0
cursor.continue.80:
  call void @al_owning_move_range(ptr %ctx, i32 %result.destination, i32 %context.value.15, i32 16, i32 16, i32 5, i32 9)
  %context.field.81 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.82 = load i32, ptr %context.field.81, align 4
  %runtime.ok.83 = icmp eq i32 %context.value.82, 0
  br i1 %runtime.ok.83, label %runtime.continue.84, label %frame.failure.0
runtime.continue.84:
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
  %context.field.85 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.86 = load i32, ptr %context.field.85, align 4
  %context.field.87 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.88 = load i32, ptr %context.field.87, align 4
  %context.field.89 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.90 = load i32, ptr %context.field.89, align 4
  %baseline.live.total.91 = add i32 %context.value.6, %context.value.8
  %current.live.total.92 = add i32 %context.value.86, %context.value.88
  %cleanup.live.delta.93 = sub i32 %baseline.live.total.91, %current.live.total.92
  %cleanup.local.delta.94 = sub i32 %context.value.8, %context.value.88
  %cleanup.reserved.delta.95 = sub i32 %context.value.90, %context.value.10
  call void @al_owning_update_live(ptr %ctx, i32 %cleanup.live.delta.93, i32 %cleanup.local.delta.94)
  call void @al_owning_local_release(ptr %ctx, i32 %cleanup.reserved.delta.95)
  call void @al_owning_leave_frame(ptr %ctx)
  ret i32 1
}


define internal i32 @agentlang_word_0000(ptr %ctx, i32 %argument.source, i32 %result.destination, i32 %depth.error) {
entry:
  %local.active.3 = alloca i1, align 1
  %local.payload.4 = alloca i32, align 4
  %local.type.5 = alloca i32, align 4
  store i1 false, ptr %local.active.3, align 1
  store i32 0, ptr %local.payload.4, align 4
  store i32 0, ptr %local.type.5, align 4
  %local.active.6 = alloca i1, align 1
  %local.payload.7 = alloca i32, align 4
  %local.type.8 = alloca i32, align 4
  store i1 false, ptr %local.active.6, align 1
  store i32 0, ptr %local.payload.7, align 4
  store i32 0, ptr %local.type.8, align 4
  %local.active.9 = alloca i1, align 1
  %local.payload.10 = alloca i32, align 4
  %local.type.11 = alloca i32, align 4
  store i1 false, ptr %local.active.9, align 1
  store i32 0, ptr %local.payload.10, align 4
  store i32 0, ptr %local.type.11, align 4
  %local.active.12 = alloca i1, align 1
  %local.payload.13 = alloca i32, align 4
  %local.type.14 = alloca i32, align 4
  store i1 false, ptr %local.active.12, align 1
  store i32 0, ptr %local.payload.13, align 4
  store i32 0, ptr %local.type.14, align 4
  %local.active.15 = alloca i1, align 1
  %local.payload.16 = alloca i32, align 4
  %local.type.17 = alloca i32, align 4
  store i1 false, ptr %local.active.15, align 1
  store i32 0, ptr %local.payload.16, align 4
  store i32 0, ptr %local.type.17, align 4
  %local.active.18 = alloca i1, align 1
  %local.payload.19 = alloca i32, align 4
  %local.type.20 = alloca i32, align 4
  store i1 false, ptr %local.active.18, align 1
  store i32 0, ptr %local.payload.19, align 4
  store i32 0, ptr %local.type.20, align 4
  %context.field.21 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 2
  %context.value.22 = load i32, ptr %context.field.21, align 4
  %context.field.23 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.24 = load i32, ptr %context.field.23, align 4
  %context.field.25 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.26 = load i32, ptr %context.field.25, align 4
  %context.field.27 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.28 = load i32, ptr %context.field.27, align 4
  %frame.entered.29 = call i32 @al_owning_enter_frame(ptr %ctx, i32 %depth.error)
  %frame.entered.ok.30 = icmp eq i32 %frame.entered.29, 0
  br i1 %frame.entered.ok.30, label %frame.entered.continue.31, label %frame.enter.failure.2
frame.enter.failure.2:
  ret i32 1
frame.entered.continue.31:
  %context.field.32 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 2
  %context.value.33 = load i32, ptr %context.field.32, align 4
  %stack.offset.34 = add i32 %context.value.33, 40
  %stack.offset.35 = add i32 %context.value.33, 56
  %reserve.status.36 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.35, i32 8)
  %reserve.ok.37 = icmp eq i32 %reserve.status.36, 0
  br i1 %reserve.ok.37, label %reserve.continue.38, label %frame.reserve.failure.1
reserve.continue.38:
  call void @al_owning_local_reserve(ptr %ctx, i32 40)
  %context.field.39 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.40 = load i32, ptr %context.field.39, align 4
  %runtime.ok.41 = icmp eq i32 %context.value.40, 0
  br i1 %runtime.ok.41, label %runtime.continue.42, label %frame.failure.0
runtime.continue.42:
  %stack.offset.43 = add i32 %argument.source, 8
  %stack.offset.44 = add i32 %stack.offset.34, 8
  call void @al_owning_move_range(ptr %ctx, i32 %stack.offset.34, i32 %argument.source, i32 8, i32 8, i32 1, i32 8)
  %context.field.45 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.46 = load i32, ptr %context.field.45, align 4
  %runtime.ok.47 = icmp eq i32 %context.value.46, 0
  br i1 %runtime.ok.47, label %runtime.continue.48, label %frame.failure.0
runtime.continue.48:
  call void @al_owning_move_range(ptr %ctx, i32 %stack.offset.44, i32 %stack.offset.43, i32 8, i32 8, i32 1, i32 8)
  %context.field.49 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.50 = load i32, ptr %context.field.49, align 4
  %runtime.ok.51 = icmp eq i32 %context.value.50, 0
  br i1 %runtime.ok.51, label %runtime.continue.52, label %frame.failure.0
runtime.continue.52:
  %step.status.53 = call i32 @al_owning_charge_step(ptr %ctx, i32 9)
  %step.ok.54 = icmp eq i32 %step.status.53, 0
  br i1 %step.ok.54, label %step.continue.55, label %frame.failure.0
step.continue.55:
  %local.is.active.56 = load i1, ptr %local.active.3, align 1
  %local.previous.payload.57 = load i32, ptr %local.payload.4, align 4
  %local.old.payload.58 = select i1 %local.is.active.56, i32 %local.previous.payload.57, i32 0
  %stack.offset.59 = add i32 %stack.offset.34, 8
  call void @al_owning_store_local(ptr %ctx, i32 %context.value.33, i32 %stack.offset.59, i32 8, i32 8, i32 8, i32 %local.old.payload.58, i32 1)
  %context.field.60 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.61 = load i32, ptr %context.field.60, align 4
  %runtime.ok.62 = icmp eq i32 %context.value.61, 0
  br i1 %runtime.ok.62, label %runtime.continue.63, label %frame.failure.0
runtime.continue.63:
  store i1 true, ptr %local.active.3, align 1
  store i32 8, ptr %local.payload.4, align 4
  store i32 1, ptr %local.type.5, align 4
  %stack.offset.64 = add i32 %stack.offset.34, 8
  call void @al_owning_drop(ptr %ctx, i32 %stack.offset.64, i32 8, i32 8, i32 1)
  %context.field.65 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.66 = load i32, ptr %context.field.65, align 4
  %runtime.ok.67 = icmp eq i32 %context.value.66, 0
  br i1 %runtime.ok.67, label %runtime.continue.68, label %frame.failure.0
runtime.continue.68:
  %stack.offset.69 = add i32 %stack.offset.34, 8
  %cursor.check.70 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.69)
  %cursor.ok.71 = icmp eq i32 %cursor.check.70, 0
  br i1 %cursor.ok.71, label %cursor.continue.72, label %frame.failure.0
cursor.continue.72:
  %step.status.73 = call i32 @al_owning_charge_step(ptr %ctx, i32 10)
  %step.ok.74 = icmp eq i32 %step.status.73, 0
  br i1 %step.ok.74, label %step.continue.75, label %frame.failure.0
step.continue.75:
  %local.is.active.76 = load i1, ptr %local.active.6, align 1
  %local.previous.payload.77 = load i32, ptr %local.payload.7, align 4
  %local.old.payload.78 = select i1 %local.is.active.76, i32 %local.previous.payload.77, i32 0
  %stack.offset.79 = add i32 %context.value.33, 8
  call void @al_owning_store_local(ptr %ctx, i32 %stack.offset.79, i32 %stack.offset.34, i32 8, i32 8, i32 8, i32 %local.old.payload.78, i32 1)
  %context.field.80 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.81 = load i32, ptr %context.field.80, align 4
  %runtime.ok.82 = icmp eq i32 %context.value.81, 0
  br i1 %runtime.ok.82, label %runtime.continue.83, label %frame.failure.0
runtime.continue.83:
  store i1 true, ptr %local.active.6, align 1
  store i32 8, ptr %local.payload.7, align 4
  store i32 1, ptr %local.type.8, align 4
  call void @al_owning_drop(ptr %ctx, i32 %stack.offset.34, i32 8, i32 8, i32 1)
  %context.field.84 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.85 = load i32, ptr %context.field.84, align 4
  %runtime.ok.86 = icmp eq i32 %context.value.85, 0
  br i1 %runtime.ok.86, label %runtime.continue.87, label %frame.failure.0
runtime.continue.87:
  %cursor.check.88 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.34)
  %cursor.ok.89 = icmp eq i32 %cursor.check.88, 0
  br i1 %cursor.ok.89, label %cursor.continue.90, label %frame.failure.0
cursor.continue.90:
  %step.status.91 = call i32 @al_owning_charge_step(ptr %ctx, i32 11)
  %step.ok.92 = icmp eq i32 %step.status.91, 0
  br i1 %step.ok.92, label %step.continue.93, label %frame.failure.0
step.continue.93:
  store i1 false, ptr %local.active.12, align 1
  store i32 0, ptr %local.payload.13, align 4
  store i32 0, ptr %local.type.14, align 4
  %step.status.94 = call i32 @al_owning_charge_step(ptr %ctx, i32 12)
  %step.ok.95 = icmp eq i32 %step.status.94, 0
  br i1 %step.ok.95, label %step.continue.96, label %frame.failure.0
step.continue.96:
  %stack.offset.97 = add i32 %stack.offset.34, 8
  %reserve.status.98 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.97, i32 13)
  %reserve.ok.99 = icmp eq i32 %reserve.status.98, 0
  br i1 %reserve.ok.99, label %reserve.continue.100, label %frame.failure.0
reserve.continue.100:
  %stack.offset.101 = add i32 %context.value.33, 8
  call void @al_owning_load_local(ptr %ctx, i32 %stack.offset.34, i32 %stack.offset.101, i32 8, i32 8, i32 1)
  %context.field.102 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.103 = load i32, ptr %context.field.102, align 4
  %runtime.ok.104 = icmp eq i32 %context.value.103, 0
  br i1 %runtime.ok.104, label %runtime.continue.105, label %frame.failure.0
runtime.continue.105:
  %stack.offset.106 = add i32 %stack.offset.34, 8
  %cursor.check.107 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.106)
  %cursor.ok.108 = icmp eq i32 %cursor.check.107, 0
  br i1 %cursor.ok.108, label %cursor.continue.109, label %frame.failure.0
cursor.continue.109:
  %step.status.110 = call i32 @al_owning_charge_step(ptr %ctx, i32 14)
  %step.ok.111 = icmp eq i32 %step.status.110, 0
  br i1 %step.ok.111, label %step.continue.112, label %frame.failure.0
step.continue.112:
  %local.is.active.113 = load i1, ptr %local.active.12, align 1
  %local.previous.payload.114 = load i32, ptr %local.payload.13, align 4
  %local.old.payload.115 = select i1 %local.is.active.113, i32 %local.previous.payload.114, i32 0
  %stack.offset.116 = add i32 %context.value.33, 24
  call void @al_owning_store_local(ptr %ctx, i32 %stack.offset.116, i32 %stack.offset.34, i32 8, i32 8, i32 8, i32 %local.old.payload.115, i32 1)
  %context.field.117 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.118 = load i32, ptr %context.field.117, align 4
  %runtime.ok.119 = icmp eq i32 %context.value.118, 0
  br i1 %runtime.ok.119, label %runtime.continue.120, label %frame.failure.0
runtime.continue.120:
  store i1 true, ptr %local.active.12, align 1
  store i32 8, ptr %local.payload.13, align 4
  store i32 1, ptr %local.type.14, align 4
  call void @al_owning_drop(ptr %ctx, i32 %stack.offset.34, i32 8, i32 8, i32 1)
  %context.field.121 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.122 = load i32, ptr %context.field.121, align 4
  %runtime.ok.123 = icmp eq i32 %context.value.122, 0
  br i1 %runtime.ok.123, label %runtime.continue.124, label %frame.failure.0
runtime.continue.124:
  %cursor.check.125 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.34)
  %cursor.ok.126 = icmp eq i32 %cursor.check.125, 0
  br i1 %cursor.ok.126, label %cursor.continue.127, label %frame.failure.0
cursor.continue.127:
  %step.status.128 = call i32 @al_owning_charge_step(ptr %ctx, i32 15)
  %step.ok.129 = icmp eq i32 %step.status.128, 0
  br i1 %step.ok.129, label %step.continue.130, label %frame.failure.0
step.continue.130:
  %stack.offset.131 = add i32 %stack.offset.34, 8
  %reserve.status.132 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.131, i32 16)
  %reserve.ok.133 = icmp eq i32 %reserve.status.132, 0
  br i1 %reserve.ok.133, label %reserve.continue.134, label %frame.failure.0
reserve.continue.134:
  %stack.offset.135 = add i32 %context.value.33, 24
  call void @al_owning_load_local(ptr %ctx, i32 %stack.offset.34, i32 %stack.offset.135, i32 8, i32 8, i32 1)
  %context.field.136 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.137 = load i32, ptr %context.field.136, align 4
  %runtime.ok.138 = icmp eq i32 %context.value.137, 0
  br i1 %runtime.ok.138, label %runtime.continue.139, label %frame.failure.0
runtime.continue.139:
  %stack.offset.140 = add i32 %stack.offset.34, 8
  %cursor.check.141 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.140)
  %cursor.ok.142 = icmp eq i32 %cursor.check.141, 0
  br i1 %cursor.ok.142, label %cursor.continue.143, label %frame.failure.0
cursor.continue.143:
  %step.status.144 = call i32 @al_owning_charge_step(ptr %ctx, i32 17)
  %step.ok.145 = icmp eq i32 %step.status.144, 0
  br i1 %step.ok.145, label %step.continue.146, label %frame.failure.0
step.continue.146:
  call void @al_owning_move_range(ptr %ctx, i32 %stack.offset.34, i32 %stack.offset.34, i32 8, i32 8, i32 1, i32 6)
  %context.field.147 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.148 = load i32, ptr %context.field.147, align 4
  %runtime.ok.149 = icmp eq i32 %context.value.148, 0
  br i1 %runtime.ok.149, label %runtime.continue.150, label %frame.failure.0
runtime.continue.150:
  %stack.offset.151 = add i32 %stack.offset.34, 8
  %stack.offset.152 = add i32 %stack.offset.34, 8
  %record.grows.153 = icmp ugt i32 %stack.offset.151, %stack.offset.152
  br i1 %record.grows.153, label %record.reserve.154, label %record.release.155
record.reserve.154:
  %reserve.status.157 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.151, i32 18)
  %reserve.ok.158 = icmp eq i32 %reserve.status.157, 0
  br i1 %reserve.ok.158, label %reserve.continue.159, label %frame.failure.0
reserve.continue.159:
  br label %record.ready.156
record.release.155:
  call void @al_owning_release_to(ptr %ctx, i32 %stack.offset.151, i32 0, i32 6, i32 8)
  %context.field.160 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.161 = load i32, ptr %context.field.160, align 4
  %runtime.ok.162 = icmp eq i32 %context.value.161, 0
  br i1 %runtime.ok.162, label %runtime.continue.163, label %frame.failure.0
runtime.continue.163:
  br label %record.ready.156
record.ready.156:
  call void @al_owning_record_layout(ptr %ctx, i32 6, i32 6, i32 %stack.offset.34, i32 8, i32 8, i32 0, i32 0)
  %context.field.164 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.165 = load i32, ptr %context.field.164, align 4
  %runtime.ok.166 = icmp eq i32 %context.value.165, 0
  br i1 %runtime.ok.166, label %runtime.continue.167, label %frame.failure.0
runtime.continue.167:
  %stack.offset.168 = add i32 %stack.offset.34, 8
  %cursor.check.169 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.168)
  %cursor.ok.170 = icmp eq i32 %cursor.check.169, 0
  br i1 %cursor.ok.170, label %cursor.continue.171, label %frame.failure.0
cursor.continue.171:
  %local.is.active.172 = load i1, ptr %local.active.12, align 1
  %local.clear.payload.173 = load i32, ptr %local.payload.13, align 4
  %local.clear.type.174 = load i32, ptr %local.type.14, align 4
  %local.clear.live.payload.175 = select i1 %local.is.active.172, i32 %local.clear.payload.173, i32 0
  %local.clear.live.type.176 = select i1 %local.is.active.172, i32 %local.clear.type.174, i32 0
  %stack.offset.177 = add i32 %context.value.33, 24
  call void @al_owning_clear_local(ptr %ctx, i32 %stack.offset.177, i32 8, i32 %local.clear.live.payload.175, i32 %local.clear.live.type.176)
  %context.field.178 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.179 = load i32, ptr %context.field.178, align 4
  %runtime.ok.180 = icmp eq i32 %context.value.179, 0
  br i1 %runtime.ok.180, label %runtime.continue.181, label %frame.failure.0
runtime.continue.181:
  store i1 false, ptr %local.active.12, align 1
  store i32 0, ptr %local.payload.13, align 4
  store i32 0, ptr %local.type.14, align 4
  %stack.offset.182 = add i32 %stack.offset.34, 8
  %cursor.check.183 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.182)
  %cursor.ok.184 = icmp eq i32 %cursor.check.183, 0
  br i1 %cursor.ok.184, label %cursor.continue.185, label %frame.failure.0
cursor.continue.185:
  %step.status.186 = call i32 @al_owning_charge_step(ptr %ctx, i32 19)
  %step.ok.187 = icmp eq i32 %step.status.186, 0
  br i1 %step.ok.187, label %step.continue.188, label %frame.failure.0
step.continue.188:
  %local.is.active.189 = load i1, ptr %local.active.9, align 1
  %local.previous.payload.190 = load i32, ptr %local.payload.10, align 4
  %local.old.payload.191 = select i1 %local.is.active.189, i32 %local.previous.payload.190, i32 0
  %stack.offset.192 = add i32 %context.value.33, 16
  call void @al_owning_store_local(ptr %ctx, i32 %stack.offset.192, i32 %stack.offset.34, i32 8, i32 8, i32 8, i32 %local.old.payload.191, i32 6)
  %context.field.193 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.194 = load i32, ptr %context.field.193, align 4
  %runtime.ok.195 = icmp eq i32 %context.value.194, 0
  br i1 %runtime.ok.195, label %runtime.continue.196, label %frame.failure.0
runtime.continue.196:
  store i1 true, ptr %local.active.9, align 1
  store i32 8, ptr %local.payload.10, align 4
  store i32 6, ptr %local.type.11, align 4
  call void @al_owning_drop(ptr %ctx, i32 %stack.offset.34, i32 8, i32 8, i32 6)
  %context.field.197 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.198 = load i32, ptr %context.field.197, align 4
  %runtime.ok.199 = icmp eq i32 %context.value.198, 0
  br i1 %runtime.ok.199, label %runtime.continue.200, label %frame.failure.0
runtime.continue.200:
  %cursor.check.201 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.34)
  %cursor.ok.202 = icmp eq i32 %cursor.check.201, 0
  br i1 %cursor.ok.202, label %cursor.continue.203, label %frame.failure.0
cursor.continue.203:
  %step.status.204 = call i32 @al_owning_charge_step(ptr %ctx, i32 20)
  %step.ok.205 = icmp eq i32 %step.status.204, 0
  br i1 %step.ok.205, label %step.continue.206, label %frame.failure.0
step.continue.206:
  store i1 false, ptr %local.active.15, align 1
  store i32 0, ptr %local.payload.16, align 4
  store i32 0, ptr %local.type.17, align 4
  store i1 false, ptr %local.active.18, align 1
  store i32 0, ptr %local.payload.19, align 4
  store i32 0, ptr %local.type.20, align 4
  %step.status.207 = call i32 @al_owning_charge_step(ptr %ctx, i32 21)
  %step.ok.208 = icmp eq i32 %step.status.207, 0
  br i1 %step.ok.208, label %step.continue.209, label %frame.failure.0
step.continue.209:
  %stack.offset.210 = add i32 %stack.offset.34, 8
  %reserve.status.211 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.210, i32 22)
  %reserve.ok.212 = icmp eq i32 %reserve.status.211, 0
  br i1 %reserve.ok.212, label %reserve.continue.213, label %frame.failure.0
reserve.continue.213:
  %stack.offset.214 = add i32 %context.value.33, 16
  call void @al_owning_load_local(ptr %ctx, i32 %stack.offset.34, i32 %stack.offset.214, i32 8, i32 8, i32 6)
  %context.field.215 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.216 = load i32, ptr %context.field.215, align 4
  %runtime.ok.217 = icmp eq i32 %context.value.216, 0
  br i1 %runtime.ok.217, label %runtime.continue.218, label %frame.failure.0
runtime.continue.218:
  %stack.offset.219 = add i32 %stack.offset.34, 8
  %cursor.check.220 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.219)
  %cursor.ok.221 = icmp eq i32 %cursor.check.220, 0
  br i1 %cursor.ok.221, label %cursor.continue.222, label %frame.failure.0
cursor.continue.222:
  %step.status.223 = call i32 @al_owning_charge_step(ptr %ctx, i32 23)
  %step.ok.224 = icmp eq i32 %step.status.223, 0
  br i1 %step.ok.224, label %step.continue.225, label %frame.failure.0
step.continue.225:
  %local.is.active.226 = load i1, ptr %local.active.15, align 1
  %local.previous.payload.227 = load i32, ptr %local.payload.16, align 4
  %local.old.payload.228 = select i1 %local.is.active.226, i32 %local.previous.payload.227, i32 0
  %stack.offset.229 = add i32 %context.value.33, 24
  call void @al_owning_store_local(ptr %ctx, i32 %stack.offset.229, i32 %stack.offset.34, i32 8, i32 8, i32 8, i32 %local.old.payload.228, i32 6)
  %context.field.230 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.231 = load i32, ptr %context.field.230, align 4
  %runtime.ok.232 = icmp eq i32 %context.value.231, 0
  br i1 %runtime.ok.232, label %runtime.continue.233, label %frame.failure.0
runtime.continue.233:
  store i1 true, ptr %local.active.15, align 1
  store i32 8, ptr %local.payload.16, align 4
  store i32 6, ptr %local.type.17, align 4
  call void @al_owning_drop(ptr %ctx, i32 %stack.offset.34, i32 8, i32 8, i32 6)
  %context.field.234 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.235 = load i32, ptr %context.field.234, align 4
  %runtime.ok.236 = icmp eq i32 %context.value.235, 0
  br i1 %runtime.ok.236, label %runtime.continue.237, label %frame.failure.0
runtime.continue.237:
  %cursor.check.238 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.34)
  %cursor.ok.239 = icmp eq i32 %cursor.check.238, 0
  br i1 %cursor.ok.239, label %cursor.continue.240, label %frame.failure.0
cursor.continue.240:
  %step.status.241 = call i32 @al_owning_charge_step(ptr %ctx, i32 24)
  %step.ok.242 = icmp eq i32 %step.status.241, 0
  br i1 %step.ok.242, label %step.continue.243, label %frame.failure.0
step.continue.243:
  %stack.offset.244 = add i32 %stack.offset.34, 8
  %reserve.status.245 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.244, i32 25)
  %reserve.ok.246 = icmp eq i32 %reserve.status.245, 0
  br i1 %reserve.ok.246, label %reserve.continue.247, label %frame.failure.0
reserve.continue.247:
  call void @al_owning_load_local(ptr %ctx, i32 %stack.offset.34, i32 %context.value.33, i32 8, i32 8, i32 1)
  %context.field.248 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.249 = load i32, ptr %context.field.248, align 4
  %runtime.ok.250 = icmp eq i32 %context.value.249, 0
  br i1 %runtime.ok.250, label %runtime.continue.251, label %frame.failure.0
runtime.continue.251:
  %stack.offset.252 = add i32 %stack.offset.34, 8
  %cursor.check.253 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.252)
  %cursor.ok.254 = icmp eq i32 %cursor.check.253, 0
  br i1 %cursor.ok.254, label %cursor.continue.255, label %frame.failure.0
cursor.continue.255:
  %step.status.256 = call i32 @al_owning_charge_step(ptr %ctx, i32 26)
  %step.ok.257 = icmp eq i32 %step.status.256, 0
  br i1 %step.ok.257, label %step.continue.258, label %frame.failure.0
step.continue.258:
  %local.is.active.259 = load i1, ptr %local.active.18, align 1
  %local.previous.payload.260 = load i32, ptr %local.payload.19, align 4
  %local.old.payload.261 = select i1 %local.is.active.259, i32 %local.previous.payload.260, i32 0
  %stack.offset.262 = add i32 %context.value.33, 32
  call void @al_owning_store_local(ptr %ctx, i32 %stack.offset.262, i32 %stack.offset.34, i32 8, i32 8, i32 8, i32 %local.old.payload.261, i32 1)
  %context.field.263 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.264 = load i32, ptr %context.field.263, align 4
  %runtime.ok.265 = icmp eq i32 %context.value.264, 0
  br i1 %runtime.ok.265, label %runtime.continue.266, label %frame.failure.0
runtime.continue.266:
  store i1 true, ptr %local.active.18, align 1
  store i32 8, ptr %local.payload.19, align 4
  store i32 1, ptr %local.type.20, align 4
  call void @al_owning_drop(ptr %ctx, i32 %stack.offset.34, i32 8, i32 8, i32 1)
  %context.field.267 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.268 = load i32, ptr %context.field.267, align 4
  %runtime.ok.269 = icmp eq i32 %context.value.268, 0
  br i1 %runtime.ok.269, label %runtime.continue.270, label %frame.failure.0
runtime.continue.270:
  %cursor.check.271 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.34)
  %cursor.ok.272 = icmp eq i32 %cursor.check.271, 0
  br i1 %cursor.ok.272, label %cursor.continue.273, label %frame.failure.0
cursor.continue.273:
  %step.status.274 = call i32 @al_owning_charge_step(ptr %ctx, i32 27)
  %step.ok.275 = icmp eq i32 %step.status.274, 0
  br i1 %step.ok.275, label %step.continue.276, label %frame.failure.0
step.continue.276:
  %stack.offset.277 = add i32 %stack.offset.34, 8
  %reserve.status.278 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.277, i32 28)
  %reserve.ok.279 = icmp eq i32 %reserve.status.278, 0
  br i1 %reserve.ok.279, label %reserve.continue.280, label %frame.failure.0
reserve.continue.280:
  %stack.offset.281 = add i32 %context.value.33, 24
  call void @al_owning_load_local(ptr %ctx, i32 %stack.offset.34, i32 %stack.offset.281, i32 8, i32 8, i32 6)
  %context.field.282 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.283 = load i32, ptr %context.field.282, align 4
  %runtime.ok.284 = icmp eq i32 %context.value.283, 0
  br i1 %runtime.ok.284, label %runtime.continue.285, label %frame.failure.0
runtime.continue.285:
  %stack.offset.286 = add i32 %stack.offset.34, 8
  %cursor.check.287 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.286)
  %cursor.ok.288 = icmp eq i32 %cursor.check.287, 0
  br i1 %cursor.ok.288, label %cursor.continue.289, label %frame.failure.0
cursor.continue.289:
  %step.status.290 = call i32 @al_owning_charge_step(ptr %ctx, i32 29)
  %step.ok.291 = icmp eq i32 %step.status.290, 0
  br i1 %step.ok.291, label %step.continue.292, label %frame.failure.0
step.continue.292:
  %stack.offset.293 = add i32 %stack.offset.34, 8
  %stack.offset.294 = add i32 %stack.offset.293, 8
  %reserve.status.295 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.294, i32 30)
  %reserve.ok.296 = icmp eq i32 %reserve.status.295, 0
  br i1 %reserve.ok.296, label %reserve.continue.297, label %frame.failure.0
reserve.continue.297:
  %stack.offset.298 = add i32 %context.value.33, 32
  call void @al_owning_load_local(ptr %ctx, i32 %stack.offset.293, i32 %stack.offset.298, i32 8, i32 8, i32 1)
  %context.field.299 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.300 = load i32, ptr %context.field.299, align 4
  %runtime.ok.301 = icmp eq i32 %context.value.300, 0
  br i1 %runtime.ok.301, label %runtime.continue.302, label %frame.failure.0
runtime.continue.302:
  %stack.offset.303 = add i32 %stack.offset.34, 16
  %cursor.check.304 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.303)
  %cursor.ok.305 = icmp eq i32 %cursor.check.304, 0
  br i1 %cursor.ok.305, label %cursor.continue.306, label %frame.failure.0
cursor.continue.306:
  %step.status.307 = call i32 @al_owning_charge_step(ptr %ctx, i32 31)
  %step.ok.308 = icmp eq i32 %step.status.307, 0
  br i1 %step.ok.308, label %step.continue.309, label %frame.failure.0
step.continue.309:
  call void @al_owning_move_range(ptr %ctx, i32 %stack.offset.34, i32 %stack.offset.34, i32 8, i32 8, i32 6, i32 6)
  %context.field.310 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.311 = load i32, ptr %context.field.310, align 4
  %runtime.ok.312 = icmp eq i32 %context.value.311, 0
  br i1 %runtime.ok.312, label %runtime.continue.313, label %frame.failure.0
runtime.continue.313:
  %stack.offset.314 = add i32 %stack.offset.34, 8
  %stack.offset.315 = add i32 %stack.offset.34, 8
  call void @al_owning_move_range(ptr %ctx, i32 %stack.offset.314, i32 %stack.offset.315, i32 8, i32 8, i32 1, i32 6)
  %context.field.316 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.317 = load i32, ptr %context.field.316, align 4
  %runtime.ok.318 = icmp eq i32 %context.value.317, 0
  br i1 %runtime.ok.318, label %runtime.continue.319, label %frame.failure.0
runtime.continue.319:
  %stack.offset.320 = add i32 %stack.offset.34, 16
  %stack.offset.321 = add i32 %stack.offset.34, 16
  %record.grows.322 = icmp ugt i32 %stack.offset.320, %stack.offset.321
  br i1 %record.grows.322, label %record.reserve.323, label %record.release.324
record.reserve.323:
  %reserve.status.326 = call i32 @al_owning_reserve_to(ptr %ctx, i32 %stack.offset.320, i32 32)
  %reserve.ok.327 = icmp eq i32 %reserve.status.326, 0
  br i1 %reserve.ok.327, label %reserve.continue.328, label %frame.failure.0
reserve.continue.328:
  br label %record.ready.325
record.release.324:
  call void @al_owning_release_to(ptr %ctx, i32 %stack.offset.320, i32 0, i32 5, i32 16)
  %context.field.329 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.330 = load i32, ptr %context.field.329, align 4
  %runtime.ok.331 = icmp eq i32 %context.value.330, 0
  br i1 %runtime.ok.331, label %runtime.continue.332, label %frame.failure.0
runtime.continue.332:
  br label %record.ready.325
record.ready.325:
  call void @al_owning_record_layout(ptr %ctx, i32 6, i32 5, i32 %stack.offset.34, i32 16, i32 16, i32 0, i32 0)
  %context.field.333 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.334 = load i32, ptr %context.field.333, align 4
  %runtime.ok.335 = icmp eq i32 %context.value.334, 0
  br i1 %runtime.ok.335, label %runtime.continue.336, label %frame.failure.0
runtime.continue.336:
  %stack.offset.337 = add i32 %stack.offset.34, 16
  %cursor.check.338 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.337)
  %cursor.ok.339 = icmp eq i32 %cursor.check.338, 0
  br i1 %cursor.ok.339, label %cursor.continue.340, label %frame.failure.0
cursor.continue.340:
  %local.is.active.341 = load i1, ptr %local.active.15, align 1
  %local.clear.payload.342 = load i32, ptr %local.payload.16, align 4
  %local.clear.type.343 = load i32, ptr %local.type.17, align 4
  %local.clear.live.payload.344 = select i1 %local.is.active.341, i32 %local.clear.payload.342, i32 0
  %local.clear.live.type.345 = select i1 %local.is.active.341, i32 %local.clear.type.343, i32 0
  %stack.offset.346 = add i32 %context.value.33, 24
  call void @al_owning_clear_local(ptr %ctx, i32 %stack.offset.346, i32 8, i32 %local.clear.live.payload.344, i32 %local.clear.live.type.345)
  %context.field.347 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.348 = load i32, ptr %context.field.347, align 4
  %runtime.ok.349 = icmp eq i32 %context.value.348, 0
  br i1 %runtime.ok.349, label %runtime.continue.350, label %frame.failure.0
runtime.continue.350:
  store i1 false, ptr %local.active.15, align 1
  store i32 0, ptr %local.payload.16, align 4
  store i32 0, ptr %local.type.17, align 4
  %local.is.active.351 = load i1, ptr %local.active.18, align 1
  %local.clear.payload.352 = load i32, ptr %local.payload.19, align 4
  %local.clear.type.353 = load i32, ptr %local.type.20, align 4
  %local.clear.live.payload.354 = select i1 %local.is.active.351, i32 %local.clear.payload.352, i32 0
  %local.clear.live.type.355 = select i1 %local.is.active.351, i32 %local.clear.type.353, i32 0
  %stack.offset.356 = add i32 %context.value.33, 32
  call void @al_owning_clear_local(ptr %ctx, i32 %stack.offset.356, i32 8, i32 %local.clear.live.payload.354, i32 %local.clear.live.type.355)
  %context.field.357 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.358 = load i32, ptr %context.field.357, align 4
  %runtime.ok.359 = icmp eq i32 %context.value.358, 0
  br i1 %runtime.ok.359, label %runtime.continue.360, label %frame.failure.0
runtime.continue.360:
  store i1 false, ptr %local.active.18, align 1
  store i32 0, ptr %local.payload.19, align 4
  store i32 0, ptr %local.type.20, align 4
  %stack.offset.361 = add i32 %stack.offset.34, 16
  %cursor.check.362 = call i32 @al_owning_check_cursor(ptr %ctx, i32 %stack.offset.361)
  %cursor.ok.363 = icmp eq i32 %cursor.check.362, 0
  br i1 %cursor.ok.363, label %cursor.continue.364, label %frame.failure.0
cursor.continue.364:
  call void @al_owning_move_range(ptr %ctx, i32 %result.destination, i32 %stack.offset.34, i32 16, i32 16, i32 5, i32 9)
  %context.field.365 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.366 = load i32, ptr %context.field.365, align 4
  %runtime.ok.367 = icmp eq i32 %context.value.366, 0
  br i1 %runtime.ok.367, label %runtime.continue.368, label %frame.failure.0
runtime.continue.368:
  %local.is.active.369 = load i1, ptr %local.active.3, align 1
  %local.clear.payload.370 = load i32, ptr %local.payload.4, align 4
  %local.clear.type.371 = load i32, ptr %local.type.5, align 4
  %local.clear.live.payload.372 = select i1 %local.is.active.369, i32 %local.clear.payload.370, i32 0
  %local.clear.live.type.373 = select i1 %local.is.active.369, i32 %local.clear.type.371, i32 0
  call void @al_owning_clear_local(ptr %ctx, i32 %context.value.33, i32 8, i32 %local.clear.live.payload.372, i32 %local.clear.live.type.373)
  %context.field.374 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.375 = load i32, ptr %context.field.374, align 4
  %runtime.ok.376 = icmp eq i32 %context.value.375, 0
  br i1 %runtime.ok.376, label %runtime.continue.377, label %frame.failure.0
runtime.continue.377:
  store i1 false, ptr %local.active.3, align 1
  store i32 0, ptr %local.payload.4, align 4
  store i32 0, ptr %local.type.5, align 4
  %local.is.active.378 = load i1, ptr %local.active.6, align 1
  %local.clear.payload.379 = load i32, ptr %local.payload.7, align 4
  %local.clear.type.380 = load i32, ptr %local.type.8, align 4
  %local.clear.live.payload.381 = select i1 %local.is.active.378, i32 %local.clear.payload.379, i32 0
  %local.clear.live.type.382 = select i1 %local.is.active.378, i32 %local.clear.type.380, i32 0
  %stack.offset.383 = add i32 %context.value.33, 8
  call void @al_owning_clear_local(ptr %ctx, i32 %stack.offset.383, i32 8, i32 %local.clear.live.payload.381, i32 %local.clear.live.type.382)
  %context.field.384 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.385 = load i32, ptr %context.field.384, align 4
  %runtime.ok.386 = icmp eq i32 %context.value.385, 0
  br i1 %runtime.ok.386, label %runtime.continue.387, label %frame.failure.0
runtime.continue.387:
  store i1 false, ptr %local.active.6, align 1
  store i32 0, ptr %local.payload.7, align 4
  store i32 0, ptr %local.type.8, align 4
  %local.is.active.388 = load i1, ptr %local.active.9, align 1
  %local.clear.payload.389 = load i32, ptr %local.payload.10, align 4
  %local.clear.type.390 = load i32, ptr %local.type.11, align 4
  %local.clear.live.payload.391 = select i1 %local.is.active.388, i32 %local.clear.payload.389, i32 0
  %local.clear.live.type.392 = select i1 %local.is.active.388, i32 %local.clear.type.390, i32 0
  %stack.offset.393 = add i32 %context.value.33, 16
  call void @al_owning_clear_local(ptr %ctx, i32 %stack.offset.393, i32 8, i32 %local.clear.live.payload.391, i32 %local.clear.live.type.392)
  %context.field.394 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.395 = load i32, ptr %context.field.394, align 4
  %runtime.ok.396 = icmp eq i32 %context.value.395, 0
  br i1 %runtime.ok.396, label %runtime.continue.397, label %frame.failure.0
runtime.continue.397:
  store i1 false, ptr %local.active.9, align 1
  store i32 0, ptr %local.payload.10, align 4
  store i32 0, ptr %local.type.11, align 4
  call void @al_owning_local_release(ptr %ctx, i32 40)
  call void @al_owning_release_to(ptr %ctx, i32 %context.value.33, i32 0, i32 0, i32 0)
  call void @al_owning_leave_frame(ptr %ctx)
  ret i32 0
frame.reserve.failure.1:
  call void @al_owning_release_to(ptr %ctx, i32 %context.value.22, i32 0, i32 0, i32 0)
  call void @al_owning_leave_frame(ptr %ctx)
  ret i32 1
frame.failure.0:
  call void @al_owning_release_to(ptr %ctx, i32 %context.value.22, i32 0, i32 0, i32 0)
  %context.field.398 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.399 = load i32, ptr %context.field.398, align 4
  %context.field.400 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.401 = load i32, ptr %context.field.400, align 4
  %context.field.402 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.403 = load i32, ptr %context.field.402, align 4
  %baseline.live.total.404 = add i32 %context.value.24, %context.value.26
  %current.live.total.405 = add i32 %context.value.399, %context.value.401
  %cleanup.live.delta.406 = sub i32 %baseline.live.total.404, %current.live.total.405
  %cleanup.local.delta.407 = sub i32 %context.value.26, %context.value.401
  %cleanup.reserved.delta.408 = sub i32 %context.value.403, %context.value.28
  call void @al_owning_update_live(ptr %ctx, i32 %cleanup.live.delta.406, i32 %cleanup.local.delta.407)
  call void @al_owning_local_release(ptr %ctx, i32 %cleanup.reserved.delta.408)
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
  %entry.input.length.ok.7 = icmp eq i32 %input.bytes, 16
  br i1 %entry.input.length.ok.7, label %entry.input.valid.8, label %entry.input.invalid.9
entry.input.invalid.9:
  call void @al_owning_set_failure(ptr %ctx, i32 4, i32 0, i32 16, i32 %input.bytes)
  br label %entry.failure.0
entry.input.valid.8:
  %reserve.status.10 = call i32 @al_owning_reserve_to(ptr %ctx, i32 16, i32 33)
  %reserve.ok.11 = icmp eq i32 %reserve.status.10, 0
  br i1 %reserve.ok.11, label %reserve.continue.12, label %entry.failure.0
reserve.continue.12:
  call void @al_owning_copy_external(ptr %ctx, i32 0, ptr %input, i32 8, i32 8, i32 1)
  %context.field.13 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.14 = load i32, ptr %context.field.13, align 4
  %runtime.ok.15 = icmp eq i32 %context.value.14, 0
  br i1 %runtime.ok.15, label %runtime.continue.16, label %entry.failure.0
runtime.continue.16:
  %input.pointer.17 = getelementptr inbounds i8, ptr %input, i32 8
  call void @al_owning_copy_external(ptr %ctx, i32 8, ptr %input.pointer.17, i32 8, i32 8, i32 1)
  %context.field.18 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.19 = load i32, ptr %context.field.18, align 4
  %runtime.ok.20 = icmp eq i32 %context.value.19, 0
  br i1 %runtime.ok.20, label %runtime.continue.21, label %entry.failure.0
runtime.continue.21:
  %entry.body.status.22 = call i32 @agentlang_entry_frame(ptr %ctx, i32 0, i32 0, i32 0)
  %entry.body.ok.23 = icmp eq i32 %entry.body.status.22, 0
  br i1 %entry.body.ok.23, label %entry.success.2, label %entry.body.failure.1
entry.body.failure.1:
  %context.field.24 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 2
  %context.value.25 = load i32, ptr %context.field.24, align 4
  %context.field.26 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.27 = load i32, ptr %context.field.26, align 4
  %context.field.28 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.29 = load i32, ptr %context.field.28, align 4
  %context.field.30 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.31 = load i32, ptr %context.field.30, align 4
  call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)
  %entry.failure.live.32 = add i32 %context.value.27, %context.value.29
  %entry.failure.delta.33 = sub i32 0, %entry.failure.live.32
  %entry.failure.local.delta.34 = sub i32 0, %context.value.29
  call void @al_owning_update_live(ptr %ctx, i32 %entry.failure.delta.33, i32 %entry.failure.local.delta.34)
  call void @al_owning_local_release(ptr %ctx, i32 %context.value.31)
  br label %entry.return.status
entry.success.2:
  call void @al_owning_publish(ptr %ctx, ptr %retained, i32 %retained.capacity, i32 0, i32 16, i32 5)
  %context.field.35 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.36 = load i32, ptr %context.field.35, align 4
  %runtime.ok.37 = icmp eq i32 %context.value.36, 0
  br i1 %runtime.ok.37, label %runtime.continue.38, label %entry.failure.0
runtime.continue.38:
  call void @al_owning_update_live(ptr %ctx, i32 -16, i32 0)
  %context.field.39 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.40 = load i32, ptr %context.field.39, align 4
  %runtime.ok.41 = icmp eq i32 %context.value.40, 0
  br i1 %runtime.ok.41, label %runtime.continue.42, label %entry.failure.0
runtime.continue.42:
  call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)
  br label %entry.return.status
entry.failure.0:
  %context.field.43 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 2
  %context.value.44 = load i32, ptr %context.field.43, align 4
  %context.field.45 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 4
  %context.value.46 = load i32, ptr %context.field.45, align 4
  %context.field.47 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 8
  %context.value.48 = load i32, ptr %context.field.47, align 4
  %context.field.49 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 6
  %context.value.50 = load i32, ptr %context.field.49, align 4
  %entry.cleanup.total.live.51 = add i32 %context.value.46, %context.value.48
  %entry.cleanup.live.delta.52 = sub i32 0, %entry.cleanup.total.live.51
  %entry.cleanup.local.delta.53 = sub i32 0, %context.value.48
  call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)
  call void @al_owning_update_live(ptr %ctx, i32 %entry.cleanup.live.delta.52, i32 %entry.cleanup.local.delta.53)
  call void @al_owning_local_release(ptr %ctx, i32 %context.value.50)
  br label %entry.return.status
entry.return.status:
  %context.field.54 = getelementptr inbounds %AlOwningContext, ptr %ctx, i32 0, i32 19
  %context.value.55 = load i32, ptr %context.field.54, align 4
  ret i32 %context.value.55
}

