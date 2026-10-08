#include "mailbox_runtime.h"
int main() { return sizeof(al_mailbox_token) == 24 ? 0 : 1; }
