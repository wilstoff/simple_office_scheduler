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
}

let calendarInstance: Calendar | null = null;

export function initCalendar(
    dotNetRef: DotNetObjectReference,
    options: CalendarOptions
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
        eventClick: (info: EventClickArg) => {
            info.jsEvent.preventDefault();
            dotNetRef.invokeMethodAsync('OnEventClicked', info.event.id, info.event.url || '');
        },
        datesSet: (info: DatesSetArg) => {
            dotNetRef.invokeMethodAsync('OnDatesChanged', info.startStr, info.endStr);
        },
        slotMinTime: '07:00:00',
        slotMaxTime: '20:00:00',
        allDaySlot: false,
        nowIndicator: true,
        eventDisplay: 'block',
        eventContent: (arg) => {
            const props = arg.event.extendedProps;
            const capacityText = props['isCancelled']
                ? 'CANCELLED'
                : `${props['signedUp']}/${props['capacity']} signed up`;

            return {
                html: `
                    <div class="fc-event-main-frame" style="padding: 2px 4px;">
                        <div class="fc-event-title-container">
                            <div class="fc-event-title" style="font-weight: bold;">${arg.event.title}</div>
                            <div style="font-size: 0.8em; opacity: 0.9;">${capacityText}</div>
                            <div style="font-size: 0.75em; opacity: 0.8;">${props['owner'] || ''}</div>
                        </div>
                    </div>
                `
            };
        },
        height: 'auto'
    });

    calendarInstance.render();
}

export function destroyCalendar(): void {
    if (calendarInstance) {
        calendarInstance.destroy();
        calendarInstance = null;
    }
}

export function refetchEvents(): void {
    calendarInstance?.refetchEvents();
}
