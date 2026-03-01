import { Calendar, EventClickArg, DatesSetArg } from '@fullcalendar/core';
import dayGridPlugin from '@fullcalendar/daygrid';
import timeGridPlugin from '@fullcalendar/timegrid';
import interactionPlugin from '@fullcalendar/interaction';

interface DotNetObjectReference {
    invokeMethodAsync(methodName: string, ...args: unknown[]): Promise<unknown>;
}

interface CalendarOptions {
    elementId: string;
    eventsUrl: string;
    initialView: string;
    selectable?: boolean;
    selectMirror?: boolean;
}

let calendarInstance: Calendar | null = null;
let calendarOwnerId: string | null = null;
let lastDotNetRef: DotNetObjectReference | null = null;
let lastOptions: CalendarOptions | null = null;
let enhancedNavRegistered = false;

function createAndRenderCalendar(
    el: HTMLElement,
    dotNetRef: DotNetObjectReference,
    options: CalendarOptions
): void {
    calendarInstance = new Calendar(el, {
        plugins: [dayGridPlugin, timeGridPlugin, interactionPlugin],
        initialView: options.initialView || 'timeGridWeek',
        headerToolbar: {
            left: 'prev,next today',
            center: 'title',
            right: 'timeGridWeek,dayGridMonth'
        },
        events: {
            url: options.eventsUrl,
            method: 'GET',
            failure: () => {
                console.error('Failed to fetch events from', options.eventsUrl);
            }
        },
        selectable: options.selectable ?? true,
        selectMirror: options.selectMirror ?? true,
        select: (info) => {
            // Format as wall-clock time without timezone offset so .NET DateTime.Parse
            // doesn't convert to the server's timezone (e.g., UTC in Docker)
            const fmt = (d: Date): string => {
                const p = (n: number) => n.toString().padStart(2, '0');
                return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:00`;
            };
            dotNetRef.invokeMethodAsync(
                'OnTimeRangeSelected',
                fmt(info.start),
                fmt(info.end),
                info.allDay
            );
            // Don't unselect — keep the highlight visible while the panel is open
        },
        unselectAuto: false,
        eventClick: (info: EventClickArg) => {
            info.jsEvent.preventDefault();
            const props = info.event.extendedProps;
            dotNetRef.invokeMethodAsync(
                'OnEventClicked',
                info.event.id,
                JSON.stringify({
                    title: info.event.title,
                    start: info.event.startStr,
                    end: info.event.endStr,
                    eventId: props['eventId'],
                    capacity: props['capacity'],
                    signedUp: props['signedUp'],
                    isCancelled: props['isCancelled'],
                    owner: props['owner'],
                    timeZoneId: props['timeZoneId'],
                    url: info.event.url || ''
                })
            );
        },
        datesSet: (info: DatesSetArg) => {
            dotNetRef.invokeMethodAsync('OnDatesChanged', info.startStr, info.endStr);
        },
        scrollTime: '07:00:00',
        slotEventOverlap: true,
        allDaySlot: false,
        nowIndicator: true,
        eventDisplay: 'block',
        eventContent: (arg) => {
            const props = arg.event.extendedProps;
            let detailText: string;
            if (props['isCancelled']) {
                detailText = 'CANCELLED';
            } else {
                const cap = `${props['signedUp']}/${props['capacity']}`;
                const owner = props['owner'] || '';
                detailText = owner ? `${cap} · ${owner}` : cap;
            }

            // Show event times in monthly view
            let timeHtml = '';
            if (arg.view.type === 'dayGridMonth' && arg.event.start) {
                const fmt = (d: Date): string => {
                    let h = d.getHours();
                    const m = d.getMinutes();
                    const ap = h >= 12 ? 'p' : 'a';
                    h = h % 12 || 12;
                    return m > 0 ? `${h}:${m.toString().padStart(2, '0')}${ap}` : `${h}${ap}`;
                };
                const s = fmt(arg.event.start);
                const e = arg.event.end ? fmt(arg.event.end) : '';
                timeHtml = `<div style="font-size:0.75em;opacity:0.85;">${s}${e ? ' - ' + e : ''}</div>`;
            }

            const fmt12 = (d: Date): string => {
                let h = d.getHours();
                const m = d.getMinutes();
                const ap = h >= 12 ? 'PM' : 'AM';
                h = h % 12 || 12;
                return m > 0 ? `${h}:${m.toString().padStart(2, '0')} ${ap}` : `${h} ${ap}`;
            };
            const timeRange = arg.event.start
                ? `${fmt12(arg.event.start)}${arg.event.end ? ' - ' + fmt12(arg.event.end) : ''}`
                : '';
            const tooltip = `${arg.event.title}\n${timeRange}\n${detailText}`;

            return {
                html: `
                    <div title="${tooltip.replace(/"/g, '&quot;')}" style="padding: 2px 4px; overflow: hidden; height: 100%;">
                        <div style="font-weight: 600; overflow: hidden; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 3;">${arg.event.title}</div>
                        ${timeHtml}
                        <div style="font-size: 0.75em; opacity: 0.85; overflow: hidden; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 3;">${detailText}</div>
                    </div>
                `
            };
        },
        height: '100%'
    });

    calendarInstance.render();

    // Watch for container size changes (e.g. sidebar CSS transition)
    // and tell FullCalendar to recalculate its layout.
    if (typeof ResizeObserver !== 'undefined') {
        new ResizeObserver(() => {
            calendarInstance?.updateSize();
        }).observe(el);
    }
}

/**
 * Checks if the calendar container was emptied (e.g. by enhanced navigation
 * DOM patching) and reinitializes if needed. Returns true if recovery occurred.
 */
export function checkAndRecover(): boolean {
    if (!lastOptions || !lastDotNetRef) return false;
    const el = document.getElementById(lastOptions.elementId);
    if (!el || el.children.length > 0) return false;

    // Container is empty — reinitialize
    if (calendarInstance) {
        try { calendarInstance.destroy(); } catch { /* already detached */ }
        calendarInstance = null;
    }
    createAndRenderCalendar(el, lastDotNetRef, lastOptions);
    return true;
}

/**
 * Registers a one-time listener for Blazor's enhanced navigation.
 * After enhanced navigation patches the DOM, FullCalendar's JS-generated
 * content may be wiped. This handler detects an empty container and
 * reinitializes the calendar using stored parameters.
 */
function registerEnhancedNavHandler(): void {
    if (enhancedNavRegistered) return;
    enhancedNavRegistered = true;

    const blazor = (window as any).Blazor;
    if (blazor?.addEventListener) {
        blazor.addEventListener('enhancedload', checkAndRecover);
    }
}

export function initCalendar(
    dotNetRef: DotNetObjectReference,
    options: CalendarOptions,
    ownerId: string
): void {
    const el = document.getElementById(options.elementId);
    if (!el) {
        console.error(`Element with id '${options.elementId}' not found`);
        return;
    }

    // Destroy previous instance if exists
    if (calendarInstance) {
        calendarInstance.destroy();
        calendarInstance = null;
    }

    calendarOwnerId = ownerId;
    lastDotNetRef = dotNetRef;
    lastOptions = options;

    createAndRenderCalendar(el, dotNetRef, options);
    registerEnhancedNavHandler();
}

export function destroyCalendar(ownerId: string): void {
    // Only destroy if this owner created the current calendar
    if (calendarInstance && calendarOwnerId === ownerId) {
        calendarInstance.destroy();
        calendarInstance = null;
        calendarOwnerId = null;
    }
}

export function refetchEvents(): void {
    calendarInstance?.refetchEvents();
}

export function resizeCalendar(): void {
    calendarInstance?.updateSize();
}

export function clearSelection(): void {
    calendarInstance?.unselect();
}
