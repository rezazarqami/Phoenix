package ir.marketsignalist.phoenix;

import org.junit.Test;
import java.time.*;
import java.util.*;
import static org.junit.Assert.*;

public class ReminderScheduleTest {
    private long at(String value,ZoneId zone){return LocalDateTime.parse(value).atZone(zone).toInstant().toEpochMilli();}
    @Test public void dailySkipsMissedOccurrencesWithoutAStorm(){
        ZoneId zone=ZoneId.of("Asia/Tehran");long start=at("2026-10-15T09:00",zone),now=at("2026-10-19T12:00",zone);
        assertEquals(at("2026-10-20T09:00",zone),ReminderSchedule.next(now,start,9,0,Collections.emptySet(),zone));
    }
    @Test public void weeklyUsesIsoDaysAndHonorsFutureStart(){
        ZoneId zone=ZoneId.of("Asia/Tehran");long start=at("2026-10-17T09:00",zone);
        assertEquals(start,ReminderSchedule.next(at("2026-10-15T12:00",zone),start,9,0,new HashSet<>(Arrays.asList(6,1)),zone));
        assertEquals(at("2026-10-19T09:00",zone),ReminderSchedule.next(start,start,9,0,new HashSet<>(Arrays.asList(6,1)),zone));
    }
    @Test public void springClockChangeKeepsNineAmRatherThanTwentyFourHours(){
        ZoneId zone=ZoneId.of("Europe/Berlin");long previous=at("2026-03-28T09:00",zone);
        long result=ReminderSchedule.next(previous,previous,9,0,Collections.emptySet(),zone);
        assertEquals(at("2026-03-29T09:00",zone),result);assertEquals(23*3600000L,result-previous);
    }
    @Test public void autumnRepeatedHourDoesNotDuplicateOccurrence(){
        ZoneId zone=ZoneId.of("Europe/Berlin");long first=at("2026-10-25T02:30",zone);
        assertEquals(at("2026-10-26T02:30",zone),ReminderSchedule.next(first,first-86400000,2,30,Collections.emptySet(),zone));
    }
}
