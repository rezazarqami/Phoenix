package ir.marketsignalist.phoenix;

import java.time.*;
import java.util.Set;

/** Calendar recurrence uses local wall time, not multiples of 24 hours. */
final class ReminderSchedule {
    static long next(long after, long start, int hour, int minute, Set<Integer> days, ZoneId zone) {
        LocalDate first=Instant.ofEpochMilli(Math.max(after,start-1)).atZone(zone).toLocalDate();
        for(int offset=0;offset<=8;offset++){
            LocalDate date=first.plusDays(offset);
            // ISO days: Monday=1 ... Sunday=7.
            if(!days.isEmpty()&&!days.contains(date.getDayOfWeek().getValue()))continue;
            long candidate=date.atTime(hour,minute).atZone(zone).toInstant().toEpochMilli();
            if(candidate>after&&candidate>=start)return candidate;
        }
        throw new IllegalArgumentException("روزهای تکرار معتبر نیست.");
    }
}
