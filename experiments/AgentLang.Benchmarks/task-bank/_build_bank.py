"""One-time, deterministic generator for the planned task-bank JSON artifacts.

This file is host-side authoring material; it is not part of an agent project.
The emitted acceptance vectors remain explicitly unreviewed/unexecuted.
"""
import json
from pathlib import Path

ROOT = Path(__file__).parent
for child in ('public', 'acceptance'):
    (ROOT/child).mkdir(parents=True, exist_ok=True)
ROWS = r'''
S01¦simple¦Classify a premium customer¦customer.premium?¦Customer -> Bool¦Return true exactly when Kind is the ordinal string "premium"; do not lowercase or trim raw data.¦¦premium
S02¦simple¦Classify an annual subscription¦subscription.annual?¦Subscription -> Bool¦Return true exactly when Term is "annual"; term text remains raw data.¦¦annual
S03¦simple¦Check active subscription status¦subscription.active?¦Subscription -> Bool¦Return true only for Active status.¦¦active
S04¦simple¦Check whether an invoice is open¦invoice.open?¦Invoice -> Bool¦Return true only for Open invoices.¦¦open
S05¦simple¦Check nonnegative product prices¦product.nonnegative-price?¦Product -> Bool¦Return true when signed Int64 unit price is zero or positive.¦¦nonnegative
S06¦simple¦Return a customer discount rate¦customer.discount-basis-points¦Customer -> Int¦Return 1000 basis points for premium customers and zero for every other raw Kind.¦S01¦discount-rate
S07¦simple¦Discount a premium customer balance¦customer.discounted-balance¦Customer -> Money¦Apply a 10% discount only to premium balances; truncate fractional minor units toward zero.¦S01,S06¦discount
S08¦simple¦Check whether a balance is nonnegative¦customer.balance-nonnegative?¦Customer -> Bool¦Return true when signed Money balance is greater than or equal to zero.¦¦balance
S09¦simple¦Compare two instants¦instant.before?¦Instant Instant -> Bool¦Compare DateTimeOffset instants, not source text; equality after UTC normalization is false.¦¦instant
S10¦simple¦Check the inclusive seven-day renewal window¦subscription.renewable-within-seven-days?¦Subscription Instant -> Bool¦Return true iff Active and expiry is between now and seven days after now, inclusive at both ends.¦S03,S09¦renewal
S11¦simple¦Calculate a checked invoice line total¦invoice.line-total¦Money Int -> Result<Money, BusinessError>¦Require quantity greater than zero and price nonnegative; multiply Int64 minor units with checked arithmetic.¦¦line
S12¦simple¦Check invoice payment eligibility¦invoice.payable?¦Invoice -> Bool¦Return true iff status is Open and total minor units are positive.¦S04,S11¦payable
S13¦simple¦Validate a full payment receipt¦payment.receipt-matches?¦Money PaymentReceipt -> Bool¦Require a nonblank reference and an exact match to the requested positive amount.¦¦receipt
S14¦simple¦Create a normalized email message¦email.message-create¦Email String String -> Result<EmailMessage, BusinessError>¦Use the reference constructor: reject whitespace-only subject/body with INVALID_EMAIL_MESSAGE and trim accepted fields.¦¦email-message
S15¦simple¦Validate an email address under the fixture policy¦email.valid?¦String -> Bool¦Match the exact bounded ASCII policy in docs/BUSINESS.md, including its 254-character maximum; do not claim full RFC validation.¦¦email-valid
S16¦simple¦Check whether a subscription can be cancelled at an instant¦subscription.cancelable-at?¦Subscription Instant -> Bool¦Return true iff Active and cancellation time is not earlier than start; equality is allowed.¦S03,S09¦cancelable
S17¦simple¦Detect an expired active subscription¦subscription.expired-at?¦Subscription Instant -> Bool¦Return true iff Active and expiry is less than or equal to the supplied instant.¦S03,S09¦expired
S18¦simple¦Calculate a premium invoice total¦invoice.discounted-total¦Customer Invoice -> Money¦Apply the 10% premium-only discount with integer truncation; leave regular totals unchanged.¦S01,S06,S11¦discounted-total
S19¦simple¦Check whether an invoice contains lines¦invoice.has-lines?¦Invoice -> Bool¦Return true for one or more lines and false for an empty list.¦¦has-lines
S20¦simple¦Compare customer identifiers by GUID identity¦customer.same-id?¦Customer Customer -> Bool¦Compare parsed CustomerId values, not spelling; F# accepts Guid.TryParse forms and formats lowercase D.¦¦id-equality
M01¦medium¦Find a subscription by nominal ID¦store.subscription¦Store SubscriptionId -> Option<Subscription>¦Search the full collection by ID; return the matching record or None without changing Store.¦S20¦lookup
M02¦medium¦Replace one subscription immutably¦store.replace-subscription¦Store Subscription -> Result<Store, BusinessError>¦Replace exactly the matching ID in place, preserving other records/order; missing ID returns SUBSCRIPTION_NOT_FOUND unchanged.¦M01¦replace
M03¦medium¦List renewal candidates¦store.renewal-candidates¦Store Instant -> List<Subscription>¦Return Active subscriptions in the inclusive seven-day window, preserving source-list order.¦S03,S09,S10,M01¦candidate-list
M04¦medium¦Build renewal reminders without duplicates¦store.renewal-reminders¦Store Instant -> Store¦Queue one reminder per eligible subscription; preserve FIFO and do not duplicate queued or sent reminders.¦M03¦reminders
M05¦medium¦Schedule renewal reminders idempotently¦store.schedule-renewal-reminder¦Store Instant -> Result<Store, BusinessError>¦Apply the reminder transition twice and retain exactly one queued/sent message per eligible subscription.¦M04¦idempotent
M06¦medium¦Create a successor subscription¦subscription.renew¦Store SubscriptionId Instant Instant -> Result<Store, BusinessError>¦Copy customer/product/term from an eligible source; use supplied dates, require expiry after start, reject duplicate IDs.¦S02,S03,S10,M01,M02¦renew
M07¦medium¦Create a discounted multi-line invoice¦invoice.create-discounted¦Store InvoiceId CustomerId List<CartLine> Instant -> Result<Store, BusinessError>¦Create all-or-nothing, snapshot product details, check line totals, then discount the invoice total once.¦S01,S06,S07,S11,M02¦invoice-discount
M08¦medium¦Reject an invalid cart without partial invoice state¦invoice.create-atomic¦Store InvoiceId CustomerId List<CartLine> Instant -> Result<Store, BusinessError>¦Validate every cart line and total before publishing; a late invalid line leaves Store unchanged.¦S11,M07¦atomic-invoice
M09¦medium¦Sum open invoice balances for one customer¦customer.open-invoice-total¦Store CustomerId -> Result<Money, BusinessError>¦Sum Open invoices for that customer with checked Int64 addition; ignore paid invoices and other customers.¦S04,S11¦open-total
M10¦medium¦List outstanding invoices for one customer¦customer.open-invoices¦Store CustomerId -> List<Invoice>¦Return only Open invoices for that customer in deterministic projection order.¦S04,M09¦open-list
M11¦medium¦Apply an exact successful payment receipt¦payment.apply-receipt¦Store PaymentId InvoiceId Money PaymentReceipt Instant -> Result<Store, BusinessError>¦On a positive exact receipt for an Open invoice, record one payment and mark Paid; mismatch/already-paid leave state unchanged.¦S12,S13¦payment-receipt
M12¦medium¦Propagate payment decline atomically¦payment.apply-result¦Store PaymentId InvoiceId Money Result<PaymentReceipt, BusinessError> Instant -> Result<Store, BusinessError>¦Propagate provider Error by stable code and leave invoice/payment state unchanged.¦M11¦payment-result
M13¦medium¦Deliver the FIFO email at the head of the queue¦email.deliver-next¦Store Result<Unit, BusinessError> -> Result<Store, BusinessError>¦On success remove exactly the first queued message and append it to sent emails, preserving order.¦¦email-success
M14¦medium¦Keep the FIFO head when email delivery fails¦email.deliver-result¦Store Result<Unit, BusinessError> -> Result<Store, BusinessError>¦A delivery Error returns EMAIL_PROVIDER_FAILURE and leaves both FIFO lists unchanged.¦M13¦email-failure
M15¦medium¦Add a customer with duplicate-ID protection¦store.add-customer-safe¦Store Customer -> Result<Store, BusinessError>¦Add immutably; reject an existing nominal CustomerId with DUPLICATE_CUSTOMER and preserve state/order.¦S20¦add-customer
M16¦medium¦Cancel active subscriptions for one customer¦customer.cancel-active-subscriptions¦Store CustomerId Instant -> Result<Store, BusinessError>¦Cancel only that customer's Active subscriptions; retain others/order. Any invalid cancellation rejects the whole transition.¦S03,S16,M02¦cancel-many
M17¦medium¦Report broken store references¦store.integrity-report¦Store -> List<BusinessError>¦Return stable sorted findings for subscriptions/invoices with missing customer or product references; do not mutate Store.¦¦integrity
M18¦medium¦Aggregate paid amounts for one customer¦customer.paid-total¦Store CustomerId -> Result<Money, BusinessError>¦Sum recorded payments linked through invoices to the customer using checked Int64 addition.¦S11,M09,M11¦paid-total
M19¦medium¦Build an invoice payment history¦invoice.payment-history¦Store InvoiceId -> List<Payment>¦Return payments for that invoice ordered by paid-at then normalized PaymentId; do not mutate Store.¦S20,M11¦payment-history
M20¦medium¦Create a premium annual renewal package¦renewal.create-package¦Store SubscriptionId Instant InvoiceId -> Result<Store, BusinessError>¦Compose eligibility, premium annual discount, invoice, successor subscription, and one reminder; any failed step preserves input Store.¦S01,S02,S07,S10,M04,M06,M07¦renew-package
D01¦debugging¦Repair premium classification¦customer.premium?¦Customer -> Bool¦Restore the ordinal Kind rule and preserve other customer behavior.¦S01,S07¦premium
D02¦debugging¦Repair discount arithmetic¦customer.discounted-balance¦Customer -> Money¦Restore the 10% premium discount with integer minor-unit arithmetic; preserve regular balances.¦S06,S07¦discount
D03¦debugging¦Repair annual term classification¦subscription.annual?¦Subscription -> Bool¦Return true only for exact "annual"; do not make monthly or case variants annual.¦S02,M06¦annual
D04¦debugging¦Repair renewal-window boundary¦subscription.renewable-within-seven-days?¦Subscription Instant -> Bool¦Apply Active status and both inclusive seven-day bounds exactly.¦S03,S09,S10¦renewal
D05¦debugging¦Repair duplicate cancellation handling¦subscription.cancel¦Store SubscriptionId Instant -> Result<Store, BusinessError>¦A second cancellation returns SUBSCRIPTION_ALREADY_CANCELLED and leaves timestamp/state unchanged.¦S16,M02¦cancel
D06¦debugging¦Repair renewal status eligibility¦store.renewal-candidates¦Store Instant -> List<Subscription>¦Exclude cancelled subscriptions inside the time window; preserve eligible active entries.¦S03,S10,M03¦candidate-list
D07¦debugging¦Repair cancellation lower boundary¦subscription.cancelable-at?¦Subscription Instant -> Bool¦Reject cancellation before start but allow equality and later instants.¦S09,S16¦cancelable
D08¦debugging¦Repair a list traversal that skips an endpoint¦store.renewal-candidates¦Store Instant -> List<Subscription>¦Visit every entry exactly once, including first and last, while retaining source order.¦M03¦candidate-list
D09¦debugging¦Repair provider failure queue loss¦email.deliver-next¦Store Result<Unit, BusinessError> -> Result<Store, BusinessError>¦On provider error retain the entire outbox and sent list unchanged.¦M13,M14¦email-failure
D10¦debugging¦Repair unchecked invoice overflow¦invoice.line-total¦Money Int -> Result<Money, BusinessError>¦Use checked Int64 multiplication, return MONEY_OVERFLOW, and publish no invoice state.¦S11,M07¦line
R01¦refactoring¦Share premium classification across customer operations¦customer.premium?¦Customer -> Bool¦Extract one typed predicate; route discount-rate and discounted-balance operations through it without behavior changes.¦S01,S06,S07¦premium¦customer.premium?¦customer.discount-basis-points,customer.discounted-balance
R02¦refactoring¦Share renewal eligibility across queries and reminders¦subscription.renewable-within-seven-days?¦Subscription Instant -> Bool¦Extract shared status/time logic; use it in candidate listing and reminder scheduling.¦S03,S09,S10,M03,M04¦renewal¦subscription.renewable-within-seven-days?¦store.renewal-candidates,store.renewal-reminders
R03¦refactoring¦Share checked line arithmetic across invoice paths¦invoice.line-total¦Money Int -> Result<Money, BusinessError>¦Route invoice creation and summary through one checked multiplication helper; retain atomic error behavior.¦S11,M07,M08¦line¦invoice.line-total¦invoice.create-discounted,invoice.create-atomic
R04¦refactoring¦Share instant comparison across renewal/cancellation¦instant.before?¦Instant Instant -> Bool¦Route time-window checks through one instant comparison helper; normalize offsets and preserve each boundary policy.¦S09,S10,S16,M03¦instant¦instant.before?¦subscription.renewable-within-seven-days?,subscription.cancelable-at?
R05¦refactoring¦Share subscription lookup before replacement¦store.subscription¦Store SubscriptionId -> Option<Subscription>¦Use one lookup word from cancellation, renewal, and replacement paths without changing missing-ID behavior.¦M01,M02,M06,M16¦lookup¦store.subscription¦store.replace-subscription,subscription.renew,customer.cancel-active-subscriptions
R06¦refactoring¦Share receipt validation across payment entry points¦payment.receipt-matches?¦Money PaymentReceipt -> Bool¦Route direct and result-provider paths through one pure receipt validator; keep failures atomic.¦S13,M11,M12¦receipt¦payment.receipt-matches?¦payment.apply-receipt,payment.apply-result
R07¦refactoring¦Share email normalization and validation¦email.message-create¦Email String String -> Result<EmailMessage, BusinessError>¦Use one constructor from direct queue and renewal-reminder creation; preserve trim and error rules.¦S14,S15,M04¦email-message¦email.message-create¦email.queue,store.renewal-reminders
R08¦refactoring¦Share open-invoice selection and aggregation¦customer.open-invoices¦Store CustomerId -> List<Invoice>¦Build total aggregation from the shared selection word so customer/status filtering agrees.¦S04,M09,M10¦open-total¦customer.open-invoices¦customer.open-invoice-total,customer.open-invoices
R09¦refactoring¦Share payment aggregation by invoice owner¦customer.paid-total¦Store CustomerId -> Result<Money, BusinessError>¦Use one owner-resolution and checked-sum path for account and dashboard summaries.¦M18,M19¦paid-total¦customer.paid-total¦customer.account-summary,store.customer-metrics
R10¦refactoring¦Route package workflows through one renewal operation¦renewal.create-package¦Store SubscriptionId Instant InvoiceId -> Result<Store, BusinessError>¦Replace duplicated premium/annual renewal sequences with one shared package operation used by one-at-a-time and batch entry points.¦M04,M06,M07,M20¦renew-package¦renewal.create-package¦renewal.run-one,renewal.run-batch
'''.strip()

def parse_rows():
    result=[]
    for line in ROWS.splitlines():
        parts=line.split('¦')
        if len(parts) not in (8,10):
            raise ValueError((len(parts), line))
        ident,cat,title,symbol,sig,behavior,prereq,profile=parts[:8]
        structural=None
        if len(parts)==10:
            structural={'helper':parts[8],'callers':parts[9].split(',')}
        result.append({'id':ident,'category':cat,'title':title,'symbol':symbol,'signature':sig,'behavior':behavior,'prerequisites':[x for x in prereq.split(',') if x],'profile':profile,'structural':structural})
    return result

def case(cid, inputs, result=None, error=None, state=None):
    return {'id':cid,'input':inputs,'expected':{'result':result,'errorCode':error,'stateProjection':state}}

EMPTY={'customers':[],'products':[],'subscriptions':[],'invoices':[],'payments':[],'outbox':[],'sentEmails':[]}
def unchanged(): return {'unchanged':True}

def vectors(profile):
    p={
    'premium':[
      case('exact-premium',{'customer':{'kind':'premium'}},True),
      case('regular',{'customer':{'kind':'regular'}},False),
      case('case-sensitive',{'customer':{'kind':'Premium'}},False)],
    'annual':[
      case('annual',{'subscription':{'term':'annual'}},True),
      case('monthly',{'subscription':{'term':'monthly'}},False),
      case('case-sensitive',{'subscription':{'term':'Annual'}},False)],
    'active':[
      case('active',{'subscription':{'status':'Active'}},True),
      case('cancelled',{'subscription':{'status':'Cancelled'}},False),
      case('cancelled-with-date',{'subscription':{'status':'Cancelled','cancelledAt':'2026-01-03T00:00:00+00:00'}},False)],
    'open':[
      case('open',{'invoice':{'status':'Open','totalMinor':500}},True),
      case('paid',{'invoice':{'status':'Paid','totalMinor':500}},False),
      case('open-zero',{'invoice':{'status':'Open','totalMinor':0}},True)],
    'nonnegative':[
      case('zero',{'product':{'unitPriceMinor':0}},True),
      case('positive',{'product':{'unitPriceMinor':199}},True),
      case('negative',{'product':{'unitPriceMinor':-1}},False)],
    'discount-rate':[
      case('premium',{'customer':{'kind':'premium'}},1000),
      case('regular',{'customer':{'kind':'regular'}},0),
      case('other',{'customer':{'kind':'vip'}},0)],
    'discount':[
      case('premium-even',{'customer':{'kind':'premium','balanceMinor':1050}},945),
      case('regular',{'customer':{'kind':'regular','balanceMinor':1050}},1050),
      case('small-premium',{'customer':{'kind':'premium','balanceMinor':1}},1)],
    'balance':[
      case('negative',{'customer':{'balanceMinor':-1}},False),
      case('zero',{'customer':{'balanceMinor':0}},True),
      case('positive',{'customer':{'balanceMinor':1}},True)],
    'instant':[
      case('before',{'left':'2026-01-01T11:00:00+00:00','right':'2026-01-01T12:00:00+00:00'},True),
      case('equal-instant-different-offset',{'left':'2026-01-01T10:00:00-02:00','right':'2026-01-01T12:00:00+00:00'},False),
      case('after',{'left':'2026-01-01T13:00:00+00:00','right':'2026-01-01T12:00:00+00:00'},False)],
    'renewal':[
      case('inclusive-seven-day-boundary',{'subscription':{'status':'Active','expiresAt':'2026-01-08T12:00:00+00:00'},'now':'2026-01-01T12:00:00+00:00'},True),
      case('one-tick-outside',{'subscription':{'status':'Active','expiresAt':'2026-01-08T12:00:00.0000001+00:00'},'now':'2026-01-01T12:00:00+00:00'},False),
      case('cancelled-inside-window',{'subscription':{'status':'Cancelled','expiresAt':'2026-01-03T12:00:00+00:00'},'now':'2026-01-01T12:00:00+00:00'},False)],
    'line':[
      case('ordinary',{'unitPriceMinor':125,'quantity':3},{'tag':'ok','value':375}),
      case('zero-quantity',{'unitPriceMinor':125,'quantity':0},None,'INVALID_QUANTITY'),
      case('int64-overflow',{'unitPriceMinor':9223372036854775807,'quantity':2},None,'MONEY_OVERFLOW')],
    'payable':[
      case('positive-open',{'invoice':{'status':'Open','totalMinor':500}},True),
      case('already-paid',{'invoice':{'status':'Paid','totalMinor':500}},False),
      case('zero-open',{'invoice':{'status':'Open','totalMinor':0}},False)],
    'receipt':[
      case('exact-match',{'requestedMinor':500,'receipt':{'reference':'ref-1','amountMinor':500}},True),
      case('amount-mismatch',{'requestedMinor':500,'receipt':{'reference':'ref-1','amountMinor':499}},False),
      case('blank-reference',{'requestedMinor':500,'receipt':{'reference':' ','amountMinor':500}},False)],
    'email-message':[
      case('trim-fields',{'to':'ada@example.test','subject':' Renewal ','body':' Notice '},{'tag':'ok','value':{'to':'ada@example.test','subject':'Renewal','body':'Notice'}}),
      case('blank-subject',{'to':'ada@example.test','subject':' ','body':'Notice'},None,'INVALID_EMAIL_MESSAGE'),
      case('blank-body',{'to':'ada@example.test','subject':'Renewal','body':'\t'},None,'INVALID_EMAIL_MESSAGE')],
    'email-valid':[
      case('ordinary',{'email':'ada@example.test'},True),
      case('plus-tag',{'email':'A_1%tag@example-domain.test'},True),
      case('empty-local-segment',{'email':'a..b@example.test'},False)],
    'cancelable':[
      case('at-start',{'subscription':{'status':'Active','startedAt':'2026-01-01T00:00:00+00:00'},'at':'2026-01-01T00:00:00+00:00'},True),
      case('before-start',{'subscription':{'status':'Active','startedAt':'2026-01-02T00:00:00+00:00'},'at':'2026-01-01T23:59:59+00:00'},False),
      case('already-cancelled',{'subscription':{'status':'Cancelled','startedAt':'2026-01-01T00:00:00+00:00'},'at':'2026-01-03T00:00:00+00:00'},False)],
    'expired':[
      case('past',{'subscription':{'status':'Active','expiresAt':'2026-01-01T00:00:00+00:00'},'at':'2026-01-02T00:00:00+00:00'},True),
      case('equal-boundary',{'subscription':{'status':'Active','expiresAt':'2026-01-02T00:00:00+00:00'},'at':'2026-01-02T00:00:00+00:00'},True),
      case('cancelled',{'subscription':{'status':'Cancelled','expiresAt':'2026-01-01T00:00:00+00:00'},'at':'2026-01-02T00:00:00+00:00'},False)],
    'discounted-total':[
      case('premium',{'customer':{'kind':'premium'},'invoice':{'totalMinor':1050}},945),
      case('regular',{'customer':{'kind':'regular'},'invoice':{'totalMinor':1050}},1050),
      case('zero',{'customer':{'kind':'premium'},'invoice':{'totalMinor':0}},0)],
    'has-lines':[
      case('one',{'invoice':{'lineCount':1}},True),
      case('many',{'invoice':{'lineCount':3}},True),
      case('empty',{'invoice':{'lineCount':0}},False)],
    'id-equality':[
      case('same-guid',{'leftId':'10000000-0000-0000-0000-000000000001','rightId':'10000000-0000-0000-0000-000000000001'},True),
      case('uppercase-guid',{'leftId':'10000000-0000-0000-0000-000000000001','rightId':'10000000-0000-0000-0000-000000000001'.upper()},True),
      case('different-guid',{'leftId':'10000000-0000-0000-0000-000000000001','rightId':'10000000-0000-0000-0000-000000000002'},False)],
    'lookup':[
      case('last-entry-found',{'subscriptions':[{'id':'s1','status':'Active'},{'id':'s2','status':'Cancelled'}],'targetId':'s2'},{'tag':'some','value':{'id':'s2','status':'Cancelled'}},state=EMPTY),
      case('missing',{'subscriptions':[{'id':'s1'}],'targetId':'s9'},{'tag':'none'},state=EMPTY),
      case('empty-list',{'subscriptions':[],'targetId':'s1'},{'tag':'none'},state=EMPTY)],
    'replace':[
      case('replace-middle',{'subscriptions':[{'id':'s1','status':'Active'},{'id':'s2','status':'Active'},{'id':'s3','status':'Active'}],'replacement':{'id':'s2','status':'Cancelled'}},{'tag':'ok','value':{'updated':True}},state={'subscriptions':[{'id':'s1','status':'Active'},{'id':'s2','status':'Cancelled'},{'id':'s3','status':'Active'}]}),
      case('missing',{'subscriptions':[{'id':'s1','status':'Active'}],'replacement':{'id':'s9','status':'Cancelled'}},None,'SUBSCRIPTION_NOT_FOUND',state={'subscriptions':[{'id':'s1','status':'Active'}]}),
      case('replace-only',{'subscriptions':[{'id':'s1','status':'Active'}],'replacement':{'id':'s1','status':'Cancelled'}},{'tag':'ok','value':{'updated':True}},state={'subscriptions':[{'id':'s1','status':'Cancelled'}]})],
    'candidate-list':[
      case('mixed-preserve-order',{'now':'2026-01-01T00:00:00+00:00','subscriptions':[{'id':'a','status':'Active','expiresAt':'2026-01-08T00:00:00+00:00'},{'id':'b','status':'Cancelled','expiresAt':'2026-01-03T00:00:00+00:00'},{'id':'c','status':'Active','expiresAt':'2026-01-02T00:00:00+00:00'}]},[{'id':'a'},{'id':'c'}],state=EMPTY),
      case('none-eligible',{'now':'2026-01-01T00:00:00+00:00','subscriptions':[{'id':'a','status':'Active','expiresAt':'2026-01-10T00:00:00+00:00'}]},[],state=EMPTY),
      case('expires-now',{'now':'2026-01-01T00:00:00+00:00','subscriptions':[{'id':'a','status':'Active','expiresAt':'2026-01-01T00:00:00+00:00'}]},[{'id':'a'}],state=EMPTY)],
    'reminders':[
      case('queue-one',{'eligibleIds':['s1'],'outbox':[],'sentEmails':[]},{'tag':'ok','value':{'queued':1}},state={'outbox':[{'subscriptionId':'s1'}],'sentEmails':[]}),
      case('already-queued',{'eligibleIds':['s1'],'outbox':[{'subscriptionId':'s1'}],'sentEmails':[]},{'tag':'ok','value':{'queued':0}},state={'outbox':[{'subscriptionId':'s1'}],'sentEmails':[]}),
      case('already-sent',{'eligibleIds':['s1'],'outbox':[],'sentEmails':[{'subscriptionId':'s1'}]},{'tag':'ok','value':{'queued':0}},state={'outbox':[],'sentEmails':[{'subscriptionId':'s1'}]})],
    'idempotent':[
      case('first',{'eligibleIds':['s1'],'outbox':[],'sentEmails':[]},{'tag':'ok','value':{'queued':1}},state={'outbox':[{'subscriptionId':'s1'}],'sentEmails':[]}),
      case('repeat',{'eligibleIds':['s1'],'outbox':[{'subscriptionId':'s1'}],'sentEmails':[]},{'tag':'ok','value':{'queued':0}},state={'outbox':[{'subscriptionId':'s1'}],'sentEmails':[]}),
      case('sent-already',{'eligibleIds':['s1'],'outbox':[],'sentEmails':[{'subscriptionId':'s1'}]},{'tag':'ok','value':{'queued':0}},state={'outbox':[],'sentEmails':[{'subscriptionId':'s1'}]})],
    'renew':[
      case('valid-successor',{'source':{'id':'s1','customerId':'c1','productId':'p1','term':'annual','status':'Active'},'newId':'s2','startedAt':'2026-01-03T00:00:00+00:00','expiresAt':'2027-01-03T00:00:00+00:00','existingIds':['s1']},{'tag':'ok','value':{'id':'s2','status':'Active','term':'annual'}},state={'subscriptionIds':['s1','s2']}),
      case('duplicate-id',{'source':{'id':'s1','customerId':'c1','productId':'p1','term':'annual','status':'Active'},'newId':'s1','startedAt':'2026-01-03T00:00:00+00:00','expiresAt':'2027-01-03T00:00:00+00:00','existingIds':['s1']},None,'DUPLICATE_SUBSCRIPTION',state={'subscriptionIds':['s1']}),
      case('expiry-not-after-start',{'source':{'id':'s1','customerId':'c1','productId':'p1','term':'annual','status':'Active'},'newId':'s2','startedAt':'2026-01-04T00:00:00+00:00','expiresAt':'2026-01-03T00:00:00+00:00','existingIds':['s1']},None,'SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START',state={'subscriptionIds':['s1']})],
    'invoice-discount':[
      case('two-lines-premium',{'customerKind':'premium','pricesMinor':[100,250],'quantities':[2,1]},{'tag':'ok','value':{'lineTotalsMinor':[200,250],'grossMinor':450,'totalMinor':405}},state={'invoiceTotalsMinor':[405]}),
      case('unknown-product',{'customerKind':'regular','pricesMinor':[100,None],'quantities':[1,1]},None,'PRODUCT_NOT_FOUND',state={'invoiceCount':0}),
      case('line-overflow',{'customerKind':'regular','pricesMinor':[9223372036854775807],'quantities':[2]},None,'MONEY_OVERFLOW',state={'invoiceCount':0})],
    'atomic-invoice':[
      case('valid-multiple',{'cart':[{'productId':'p1','quantity':1},{'productId':'p2','quantity':2}],'products':[{'id':'p1','unitPriceMinor':100},{'id':'p2','unitPriceMinor':50}]},{'tag':'ok','value':{'lineCount':2,'totalMinor':200}},state={'invoiceCount':1}),
      case('late-missing-product',{'cart':[{'productId':'p1','quantity':1},{'productId':'missing','quantity':1}],'products':[{'id':'p1','unitPriceMinor':100}]},None,'PRODUCT_NOT_FOUND',state={'invoiceCount':0}),
      case('late-invalid-quantity',{'cart':[{'productId':'p1','quantity':1},{'productId':'p2','quantity':0}],'products':[{'id':'p1','unitPriceMinor':100},{'id':'p2','unitPriceMinor':50}]},None,'INVALID_QUANTITY',state={'invoiceCount':0})],
    'open-total':[
      case('mixed-owner-and-status',{'customerId':'c1','invoices':[{'customerId':'c1','status':'Open','totalMinor':100},{'customerId':'c1','status':'Paid','totalMinor':50},{'customerId':'c2','status':'Open','totalMinor':300}]},{'tag':'ok','value':100},state=EMPTY),
      case('no-open-invoices',{'customerId':'c1','invoices':[{'customerId':'c2','status':'Open','totalMinor':300}]},{'tag':'ok','value':0},state=EMPTY),
      case('checked-overflow',{'customerId':'c1','invoices':[{'customerId':'c1','status':'Open','totalMinor':9223372036854775807},{'customerId':'c1','status':'Open','totalMinor':1}]},None,'MONEY_OVERFLOW',state=EMPTY)],
    'open-list':[
      case('filter',{'customerId':'c1','invoices':[{'id':'i1','customerId':'c1','status':'Open'},{'id':'i2','customerId':'c1','status':'Paid'},{'id':'i3','customerId':'c2','status':'Open'}]},[{'id':'i1'}],state=EMPTY),
      case('empty',{'customerId':'c1','invoices':[]},[],state=EMPTY),
      case('other-customer',{'customerId':'c1','invoices':[{'id':'i2','customerId':'c2','status':'Open'}]},[],state=EMPTY)],
    'payment-receipt':[
      case('exact-success',{'invoice':{'id':'i1','status':'Open','totalMinor':500},'receipt':{'reference':'r1','amountMinor':500},'paymentId':'p1'},{'tag':'ok','value':{'paymentId':'p1'}},state={'invoices':[{'id':'i1','status':'Paid'}],'payments':[{'id':'p1','invoiceId':'i1','amountMinor':500}]}),
      case('receipt-mismatch',{'invoice':{'id':'i1','status':'Open','totalMinor':500},'receipt':{'reference':'r1','amountMinor':499},'paymentId':'p1'},None,'INVALID_PROVIDER_RECEIPT',state={'invoices':[{'id':'i1','status':'Open'}],'payments':[]}),
      case('already-paid',{'invoice':{'id':'i1','status':'Paid','totalMinor':500},'receipt':{'reference':'r1','amountMinor':500},'paymentId':'p1'},None,'INVOICE_ALREADY_PAID',state={'invoices':[{'id':'i1','status':'Paid'}],'payments':[]})],
    'payment-result':[
      case('provider-decline',{'invoice':{'id':'i1','status':'Open','totalMinor':500},'providerResult':{'tag':'error','errorCode':'PAYMENT_PROVIDER_FAILURE'},'paymentId':'p1'},None,'PAYMENT_PROVIDER_FAILURE',state={'invoices':[{'id':'i1','status':'Open'}],'payments':[]}),
      case('invalid-receipt',{'invoice':{'id':'i1','status':'Open','totalMinor':500},'providerResult':{'tag':'ok','reference':'r1','amountMinor':499},'paymentId':'p1'},None,'INVALID_PROVIDER_RECEIPT',state={'invoices':[{'id':'i1','status':'Open'}],'payments':[]}),
      case('provider-success',{'invoice':{'id':'i1','status':'Open','totalMinor':500},'providerResult':{'tag':'ok','reference':'r1','amountMinor':500},'paymentId':'p1'},{'tag':'ok','value':{'paymentId':'p1'}},state={'invoices':[{'id':'i1','status':'Paid'}],'payments':[{'id':'p1','invoiceId':'i1','amountMinor':500}]})],
    'email-success':[
      case('two-queued',{'outbox':[{'id':'e1'},{'id':'e2'}],'sentEmails':[],'providerResult':'ok'},{'tag':'ok','value':{'delivered':'e1'}},state={'outbox':[{'id':'e2'}],'sentEmails':[{'id':'e1'}]}),
      case('append-after-existing',{'outbox':[{'id':'e1'}],'sentEmails':[{'id':'e0'}],'providerResult':'ok'},{'tag':'ok','value':{'delivered':'e1'}},state={'outbox':[],'sentEmails':[{'id':'e0'},{'id':'e1'}]}),
      case('empty',{'outbox':[],'sentEmails':[],'providerResult':'ok'},None,'NO_PENDING_EMAIL',state={'outbox':[],'sentEmails':[]})],
    'email-failure':[
      case('provider-error',{'outbox':[{'id':'e1'},{'id':'e2'}],'sentEmails':[],'providerResult':'error'},None,'EMAIL_PROVIDER_FAILURE',state={'outbox':[{'id':'e1'},{'id':'e2'}],'sentEmails':[]}),
      case('success',{'outbox':[{'id':'e1'}],'sentEmails':[],'providerResult':'ok'},{'tag':'ok','value':{'delivered':'e1'}},state={'outbox':[],'sentEmails':[{'id':'e1'}]}),
      case('empty',{'outbox':[],'sentEmails':[],'providerResult':'error'},None,'NO_PENDING_EMAIL',state={'outbox':[],'sentEmails':[]})],
    'add-customer':[
      case('new-id',{'customers':[{'id':'c1'}],'customer':{'id':'c2'}},{'tag':'ok','value':{'added':'c2'}},state={'customerIds':['c1','c2']}),
      case('duplicate-id',{'customers':[{'id':'c1','kind':'regular'}],'customer':{'id':'c1','kind':'premium'}},None,'DUPLICATE_CUSTOMER',state={'customerIds':['c1']}),
      case('empty-store',{'customers':[],'customer':{'id':'c1'}},{'tag':'ok','value':{'added':'c1'}},state={'customerIds':['c1']})],
    'cancel-many':[
      case('mixed',{'customerId':'c1','at':'2026-02-01T00:00:00+00:00','subscriptions':[{'id':'s1','customerId':'c1','status':'Active'},{'id':'s2','customerId':'c1','status':'Cancelled'},{'id':'s3','customerId':'c2','status':'Active'}]},{'tag':'ok','value':{'cancelledIds':['s1']}},state={'subscriptions':[{'id':'s1','status':'Cancelled'},{'id':'s2','status':'Cancelled'},{'id':'s3','status':'Active'}]}),
      case('no-matching-customer',{'customerId':'c9','at':'2026-02-01T00:00:00+00:00','subscriptions':[{'id':'s1','customerId':'c1','status':'Active'}]},{'tag':'ok','value':{'cancelledIds':[]}},state={'subscriptions':[{'id':'s1','status':'Active'}]}),
      case('pre-start-rejects-all',{'customerId':'c1','at':'2025-12-31T00:00:00+00:00','subscriptions':[{'id':'s1','customerId':'c1','status':'Active','startedAt':'2026-01-01T00:00:00+00:00'}]},None,'CANCEL_BEFORE_START',state={'subscriptions':[{'id':'s1','status':'Active'}]})],
    'integrity':[
      case('two-orphans',{'customers':['c1'],'products':['p1'],'subscriptions':[{'id':'s1','customerId':'c9','productId':'p1'}],'invoices':[{'id':'i1','customerId':'c1','productIds':['p9']}]},[{'code':'CUSTOMER_NOT_FOUND','entityId':'s1'},{'code':'PRODUCT_NOT_FOUND','entityId':'i1'}],state=EMPTY),
      case('healthy',{'customers':['c1'],'products':['p1'],'subscriptions':[{'id':'s1','customerId':'c1','productId':'p1'}],'invoices':[]},[],state=EMPTY),
      case('stable-sorted-findings',{'customers':[],'products':[],'subscriptions':[{'id':'s2','customerId':'c2','productId':'p2'},{'id':'s1','customerId':'c1','productId':'p1'}],'invoices':[]},[{'code':'CUSTOMER_NOT_FOUND','entityId':'s1'},{'code':'PRODUCT_NOT_FOUND','entityId':'s1'},{'code':'CUSTOMER_NOT_FOUND','entityId':'s2'},{'code':'PRODUCT_NOT_FOUND','entityId':'s2'}],state=EMPTY)],
    'paid-total':[
      case('two-owned-one-other',{'customerId':'c1','invoices':[{'id':'i1','customerId':'c1'},{'id':'i2','customerId':'c2'}],'payments':[{'invoiceId':'i1','amountMinor':250},{'invoiceId':'i1','amountMinor':125},{'invoiceId':'i2','amountMinor':900}]},{'tag':'ok','value':375},state=EMPTY),
      case('no-payment',{'customerId':'c1','invoices':[{'id':'i1','customerId':'c1'}],'payments':[]},{'tag':'ok','value':0},state=EMPTY),
      case('overflow',{'customerId':'c1','invoices':[{'id':'i1','customerId':'c1'}],'payments':[{'invoiceId':'i1','amountMinor':9223372036854775807},{'invoiceId':'i1','amountMinor':1}]},None,'MONEY_OVERFLOW',state=EMPTY)],
    'payment-history':[
      case('filter-and-sort',{'invoiceId':'i1','payments':[{'id':'p2','invoiceId':'i1','paidAt':'2026-01-02T00:00:00+00:00'},{'id':'p3','invoiceId':'i2','paidAt':'2026-01-01T00:00:00+00:00'},{'id':'p1','invoiceId':'i1','paidAt':'2026-01-01T00:00:00+00:00'}]},[{'id':'p1'},{'id':'p2'}],state=EMPTY),
      case('stable-id-tie-break',{'invoiceId':'i1','payments':[{'id':'p2','invoiceId':'i1','paidAt':'2026-01-01T00:00:00+00:00'},{'id':'p1','invoiceId':'i1','paidAt':'2026-01-01T00:00:00+00:00'}]},[{'id':'p1'},{'id':'p2'}],state=EMPTY),
      case('none',{'invoiceId':'i9','payments':[{'id':'p1','invoiceId':'i1','paidAt':'2026-01-01T00:00:00+00:00'}]},[],state=EMPTY)],
    'renew-package':[
      case('premium-annual',{'kind':'premium','term':'annual','eligible':True,'grossMinor':10000},{'tag':'ok','value':{'totalMinor':9000}},state={'invoiceCount':1,'subscriptionCount':2,'outboxCount':1}),
      case('regular-monthly',{'kind':'regular','term':'monthly','eligible':True,'grossMinor':10000},{'tag':'ok','value':{'totalMinor':10000}},state={'invoiceCount':1,'subscriptionCount':2,'outboxCount':1}),
      case('ineligible-unchanged',{'kind':'premium','term':'annual','eligible':False,'grossMinor':10000},None,'SUBSCRIPTION_NOT_ELIGIBLE',state={'invoiceCount':0,'subscriptionCount':1,'outboxCount':0})],
    'cancel':[
      case('first-cancel',{'status':'Active','startedAt':'2026-01-01T00:00:00+00:00','at':'2026-01-03T00:00:00+00:00'},{'tag':'ok','value':{'status':'Cancelled'}},state={'status':'Cancelled','cancelledAt':'2026-01-03T00:00:00+00:00'}),
      case('repeat-cancel',{'status':'Cancelled','startedAt':'2026-01-01T00:00:00+00:00','cancelledAt':'2026-01-03T00:00:00+00:00','at':'2026-01-04T00:00:00+00:00'},None,'SUBSCRIPTION_ALREADY_CANCELLED',state={'status':'Cancelled','cancelledAt':'2026-01-03T00:00:00+00:00'}),
      case('before-start',{'status':'Active','startedAt':'2026-01-03T00:00:00+00:00','at':'2026-01-02T00:00:00+00:00'},None,'CANCEL_BEFORE_START',state={'status':'Active'})]
    }
    if profile not in p: raise KeyError(profile)
    return p[profile]

def snapshot(snapshot_id):
    return {'id':snapshot_id,'sha256':None,'status':'pending'}

def emit():
    rows=parse_rows()
    if len(rows)!=60: raise ValueError(f'expected 60 tasks, got {len(rows)}')
    counts={name:sum(1 for r in rows if r['category']==name) for name in ('simple','medium','debugging','refactoring')}
    if counts!={'simple':20,'medium':20,'debugging':10,'refactoring':10}: raise ValueError(counts)
    manifest={'schemaVersion':1,'suiteId':'agentlang-business-60-v1','status':'planned','executable':False,
      'description':'Proposed 60-task bank; vectors pending host review/execution.',
      'categoryCounts':counts,'modes':['flat','growing','conventional'],'tasks':[]}
    for r in rows:
        if r['category'] in ('simple','medium'):
            track='flat-and-growing'
            prerequisitePolicy='growing-task-dependencies'
            pins={'flat':snapshot('business-schema-flat-v1'),
                  'growing':snapshot('growth-before-'+r['id']),
                  'conventional':snapshot('business-fsharp-candidate-v1')}
            order=int(r['id'][1:]) if r['id'].startswith('S') else 20+int(r['id'][1:])
        elif r['category']=='debugging':
            track='isolated-adversarial'
            prerequisitePolicy='known-good-fixture-recipe'
            pins={'flat':snapshot('debug-'+r['id']+'-flat-fixture'),
                  'growing':snapshot('debug-'+r['id']+'-prerequisite-fixture'),
                  'conventional':snapshot('debug-'+r['id']+'-fsharp-fixture')}
            order=None
        else:
            track='isolated-refactor'
            prerequisitePolicy='known-good-fixture-recipe'
            pins={'flat':snapshot('refactor-'+r['id']+'-flat-fixture'),
                  'growing':snapshot('refactor-'+r['id']+'-prerequisite-fixture'),
                  'conventional':snapshot('refactor-'+r['id']+'-fsharp-fixture')}
            order=None
        public_file=f"public/{r['id']}.json"
        acceptance_file=f"acceptance/{r['id']}.json"
        contract={'symbol':r['symbol'],'signature':r['signature'],'behavior':r['behavior']}
        if r['structural']:
            contract['structuralRequirement']={'helper':r['structural']['helper'],'calledBy':r['structural']['callers']}
        manifest['tasks'].append({
          'id':r['id'],'category':r['category'],'difficulty':r['category'],'title':r['title'],
          'retentionTrack':track,'prerequisitePolicy':prerequisitePolicy,'growthOrder':order,
          'prerequisites':r['prerequisites'],'requiredPublicContract':contract,
          'snapshotPins':pins,'status':'planned','executable':False,
          'adapterEvidence':{'agentlang':None,'conventional':None,'independentOracle':None},
          'acceptanceEvidence':{'status':'pending','evidenceRef':None,'sha256':None},
          'publicFile':public_file,'acceptanceFile':acceptance_file})
        public={'schemaVersion':1,'id':r['id'],'category':r['category'],
          'goal':r['behavior']+' Implement the named public operation and attach tests for the listed behavior. Inspect available vocabulary first and preserve unrelated behavior.',
          'initialContext':'The host supplies a deterministic in-memory business fixture, task types, and runtime vocabulary when this task is executable. Follow the supplied field mappings and public contract; preserve unrelated behavior.',
          'requiredPublicContract':contract}
        (ROOT/public_file).write_text(json.dumps(public,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
        cases=vectors(r['profile'])
        if r['category']=='debugging':
            for c in cases:
                if c['expected']['stateProjection'] is None: c['expected']['stateProjection']=unchanged()
        provenance={'status':'proposed-pending-host-review',
          'basis':['docs/BUSINESS.md','experiments/AgentLang.Business/Business.fs','docs/BUSINESS-LANGUAGE-PLAN.md'],
          'note':'Existing reference operations have not been run against this vector. New policy outcomes are proposals under the public contract.'}
        acceptance={'schemaVersion':1,'taskId':r['id'],'vectorStatus':'proposed-pending-host-review',
          'executionStatus':'pending','adapters':{'agentlang':'pending','conventional':'pending','referenceCrossCheck':'pending'},
          'provenance':provenance,'cases':cases}
        if r['category']=='debugging':
            acceptance['fixtureSetup']={'kind':'single-defect','mutationId':'mutant-'+r['id'].lower(),'status':'pending-host-fixture'}
        if r['structural']:
            acceptance['structuralWitness']={'kind':'symbol-call-graph','helper':r['structural']['helper'],
              'calledBy':r['structural']['callers'],'conventionalStatus':'pending-callgraph-adapter',
              'agentlangStatus':'pending-runtime-adapter','substringEvidenceAllowed':False}
        (ROOT/acceptance_file).write_text(json.dumps(acceptance,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
    (ROOT/'manifest.json').write_text(json.dumps(manifest,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
    total=sum(len(vectors(r['profile'])) for r in rows)
    print(f'generated tasks={len(rows)} categories={counts} vectors={total}')

if __name__=='__main__': emit()
