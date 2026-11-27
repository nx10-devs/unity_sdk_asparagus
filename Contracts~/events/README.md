# Our events

This folder holds what an SDK ships about events: the list of our events, and the rules for them. The SDK repository copies both files, as they are.

| File | What it is |
| --- | --- |
| `core_events.json` | The core events with their fields, the spans with the names they are sent as, the standard state keys, the SDK's own state keys, and the two outcome texts. One command writes it from the Python lists, and nobody edits it by hand. |
| `README.md` | This file: what the JSON file holds, and the eighteen rules. It is written by hand. |

The lists themselves are in `packages/asparagus-data-processing/events`. That folder's `AGENTS.md` says how a list is changed, and gives the command that writes the JSON file again. `@nx10/asparagus-contract` exports the contents of the JSON file to TypeScript.

The list is a recommendation, and never a gate. Rule 1 says so, and every other rule keeps to it.

## What `core_events.json` holds

| Part | What it holds |
| --- | --- |
| `events` | The eleven core events, in an order that does not change |
| `spans` | The ten span names of ours |
| `state_keys` | The three standard state keys |
| `sdk_state_keys` | The SDK's own state keys, which the SDK sets itself |
| `outcome_converted` | The text `Converted` |
| `outcome_unconverted` | The text `UnConverted` |

An event:

| Part | What it holds |
| --- | --- |
| `name` | The event's name |
| `meaning` | One sentence |
| `span` | The span of ours that the event opens or closes. `null` when it opens and closes none. |
| `role` | What the event does to its span: `OPENS`, `CLOSES` or `NONE` |
| `carries_outcome` | True when a core call for the event always carries an outcome. Any event may carry one. |
| `fields` | The event's fields, in an order that does not change |

A field:

| Part | What it holds |
| --- | --- |
| `key` | The field's key |
| `type` | `TEXT`, `NUMBER` or `FLAG`. These are the three kinds of value in the batch format. A flag is true or false. |
| `expected` | True when a core call always sends the field. It never means that an event is checked. A core event that arrives without one is kept, and the event monitor counts it, so that we can ask the studio for it. |
| `meaning` | One sentence |
| `values` | The values we know, for some text fields. They are not a limit: another value is kept as sent. |

A span:

| Part | What it holds |
| --- | --- |
| `name` | Our name for the span |
| `meaning` | One sentence |
| `sent_as` | The names that mean this span, each a whole name |
| `sent_as_prefixes` | Any name that starts with one of these means this span as well, unless it is a whole name of a span: every whole name is asked first |

A state key:

| Part | What it holds |
| --- | --- |
| `key` | Our key |
| `default_attribute` | The game's attribute that feeds the key, for a game that has no mapping row for it |
| `meaning` | One sentence or two |

## The spans

Ten span names of ours:

| Our span | Sent as | Opened by | Closed by |
| --- | --- | --- | --- |
| `level` | `level` | `game_level_started` | `game_level_succeeded`, `game_level_failed` |
| `ad` | `ad`, and any name that starts with `ad_` | `ad_started` | `ad_finished` |
| `purchase_view` | `purchase_view` | `in_app_purchase_view_opened` | `in_app_purchase_view_closed` |
| `round` | `round` | `gambling_round_started` | `gambling_round_finished` |
| `rewarded_offer` | `rewarded_offer` | none | none |
| `backgrounded` | `backgrounded` | none | none |
| `settings` | `settings` | none | none |
| `popup` | `popup` | none | none |
| `tutorial_or_onboarding` | `tutorial_or_onboarding` | none | none |
| `banner` | `banner`, `ad_banner` | none | none |

An ad's span is sent as `ad_` followed by the value of its `type`, such as `ad_interstitial`. A banner can stay up while another ad plays, and two spans that overlap need two names, so a banner's span, `ad_banner` or `banner`, is our span `banner`. With no `type`, an ad's span is sent as `ad`. On the server every other name that starts with `ad_` is our span `ad`, and the type is a field.

## The outcome

An outcome is the text `Converted` or `UnConverted`, spelled exactly so, as games send it today.

- **These are the SDK's outcome events.** `ad_revenue` and `in_app_purchase` are the two that the studio docs publish, with the same fields, and a core call for either always carries an outcome.
- **Any event may carry one.** `SendOutcomeEvent` takes any name, as it does today. The outcome travels beside the event's fields, and is stored with it.
- **Any other text is kept as sent,** and counts as no outcome of ours.

## The standard state keys

Three keys of ours say what the player is doing. Each is fed by one attribute of the game.

| Our key | Fed by the game's attribute | Holds |
| --- | --- | --- |
| `level` | `game_level` | The level the player is in |
| `screen` | `view` | The screen the player is on |
| `mode` | `mode` | The kind of play, from a short list of the game's own |

A game that uses another attribute is given a mapping row.

## The SDK's own state keys

The SDK sets these keys of the state itself, and a game sets none. Their names start with `nx10_`.

| Key | Type | What it means |
| --- | --- | --- |
| `nx10_screen_brightness` | `NUMBER` | The brightness of the screen as the system reports it, from 0, the least, to 1, the most. |
| `nx10_screen_locked` | `FLAG` | True while the device is locked, and false while it is not. |

A new value of the brightness is sent when it differs from the last one sent by at least 0.05, and never more than once a second. The lock is sent at each change.

## The rules

Eighteen rules, under numbers that do not change. The words they use:

- **A core event** is one of the eleven events of `core_events.json`, and **a core call** is the SDK's own call that sends one.
- **The marker** says whether an event opens a span or closes one, and **the number** is the span's number within its session. The SDK sends both with the event, as `span_marker` and `span_number` in the batch format.
- **A mapping row** says, for one game, which of our names one of the game's names is. The rows are on the server.
- **Turnip** is the data stack that came before this one.

### Events

1. Anything is accepted. A game may send any event name, with any fields, under any span name. It is sent and stored under the name the game gave it, with its fields as the game gave them. Nothing is refused, dropped or renamed for not being on our list.
2. The core events are recommended, and a core call is only a shortcut. It sends the event's name, its fields under the keys of the list, and its span name, and it adds nothing else. An event sent by hand under a core name is the same event.
3. Names and keys are for good. Once an SDK has shipped one, it is never renamed and never used for something else.

### Spans, on the phone

4. The role belongs to the event's name. A core event that opens always opens, and one that closes always closes, however it was sent.
5. The span name is the caller's when the caller gives one. With none, a core event that opens or closes gets its own: `level`, `purchase_view`, `round`, or for an ad `ad_` and the value of `type`. Any other event with no span name is part of no span.
6. An event with no role of its own is a game's own event, or a core event that neither opens nor closes. Sent with a span name, it opens a span when none of that name is open. When a span of that name is open, what opened it decides. If an event with no role opened it, this event closes it: so the first opens, the next closes, the one after that opens a new span, and so on. If a core event opened it, this event opens and closes nothing: it carries the span name with no marker, and only a core event closes that span.
7. A core event that opens, while a span of the same name is open, ends the open one as "no end seen" and opens a new one. The event carries the new span's number.
8. A core event that closes, with no span of that name open, is marked as a close and carries the number 0. It makes no span.
9. Two spans of the same name are never open at once. Spans of different names may be.
10. The SDK numbers the session's spans from 1, in the order they open.
11. The span state belongs to the session. A session starts with no open span and counts from 1. Nothing else resets it: not a pause, not a return from the background, not a stop of capture. The SDK never makes up an event to close a span. A span that is open when the session ends stays open.

### Spans, on the server

12. The marker and the number are stored as sent, and nothing rewrites them. The collector writes its spans by them.
13. When a core event that opens or closes carries a marker that is not the one its role gives, the collector still goes by the marker. It flags the event, and the event monitor counts it. SQL can build that session's spans again from the names. An event with no role of its own has no marker that could be wrong: by rule 6 it may carry either, or none.
14. An event with a span name and no marker makes no span in the collector. When its batch lists the open spans, even if the list is empty, the sender marks spans, and the event is only part of a span that a core event opened (rule 6). When its batch has no list of open spans, the sender does not mark spans, and SQL builds the span from the names.
15. Turnip's data carries no span name and no marker. The event that a row maps to gives our span and the role, and SQL builds the spans by rules 4 and 7.
16. A span name as sent is given our span name by the table under "The spans". Any other name is the game's own: a mapping row may give it one of ours, and otherwise it is kept as sent with no span of ours.

### Resolving a name

17. A mapping row for that game wins. Otherwise a core name means itself. Anything else is kept as sent and counted as unmapped.

### State

18. A state key that starts with `nx10_` is the SDK's own. The SDK sets it itself, and a game sets none. The backend accepts it as it accepts every key of the state, and stores it with the rest.
