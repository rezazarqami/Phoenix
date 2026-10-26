# Risk-free exit labels

The trading thresholds are unchanged and verified by `StagedRiskProtectionTests` for Long and Short:

- At 50% of entry-to-target progress, place a reduce-only stop-limit at 20% of that distance.
- At 75% progress, establish the second stop-limit at 50%, then the backup stop-market at 20%, then confirm cancellation of the first limit. The replacement is installed before cancelling the older protection.
- Prices are normalized to the instrument tick. All percentages refer to the entry-to-target price distance, not the stop-to-target distance or account P&L.

`FilledAsync` now records `RiskFreeCloseOrderId` and `RiskFreeCloseKind` from the actual filled exchange order ID. These fields survive queue cloning, persistence and historical archival in both strategies. An early limit filled during an attempted stage-2 upgrade remains a first-step closure; `ProtectionStage` is not used to guess the exit order.

Telegram text and image results use the same labels:

| Actual filled order | Title | Order detail |
| --- | --- | --- |
| EarlyStopLimitOrderId | پلهٔ اول ریسک‌فری | استاپ‌لیمیت، سطح ۲۰٪ |
| StopLoss2OrderId | پلهٔ دوم ریسک‌فری | استاپ‌لیمیت، سطح ۵۰٪ |
| RiskFreeStopMarketOrderId | محافظ ریسک‌فری | استاپ‌مارکت، سطح ۲۰٪ |

Public/dedicated channel notifications, private trading notifications and Strategy 2 all use the shared formatter. Legacy results without a recorded filled order remain generic and explicitly state that the exit order type was not recorded. Existing notifications are not rewritten. No live Telegram messages or exchange orders are sent by the tests.
