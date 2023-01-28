using System;
using System.Collections.Generic;

/// <summary>
/// C 4 pluses language GGPO.
/// </summary>

public class C4PGGPO<InputType>
{
    #region Internal data
    CSGGPOEvents<InputType> EventCallbacks;

    StateStorage<byte> SaveStates;

    InputStorage<InputType> InputHistory;

    List<(int tickItWasPressed, int playerID, InputType input)> FutureInputs;
    /// <summary>
    /// PlayerID as key, LastInputSent as value.
    /// </summary>
    InternalDictionary32<int, int> Players;

    List<int> RemovedPlayers;

    int TickOffset = 0;

    int MaxInputAhead = 50;

    int PresentTick = -1;

    int CurrTick = -1;

    (int responsibleId, int tick) TickToProcess = (int.MinValue, int.MaxValue);
    #endregion

    #region Properties
    public int SavestateCount => SaveStates.Count;
    public int CurrentTick => CurrTick + TickOffset;
    public int NowTick
    {
        get => PresentTick + TickOffset;
        set
        {
            value -= TickOffset;

            int difference = value - PresentTick;

            CurrTick += difference;

            PresentTick = value;
        }
    }
    InternalDictionary32<int, int> ConfirmedInputs => Players;
    #endregion

    /// <summary>
    /// If you're looking for what each parameter does, please see their description.
    /// </summary>
    /// <param name="tick"> The tick this instance starts on.</param>
    /// <param name="maxFutureInput">How much 'in the future' a input can be.</param>
    /// <param name="bufferSize">The amount of bytes in each state.</param>
    /// <param name="stateNum">Max range of states this can store for rollbacks.</param>
    /// <param name="events">Events that are NECESSARY for you to fill up.</param>
    public C4PGGPO(int tick, int maxFutureInput, int bufferSize, int stateNum, CSGGPOEvents<InputType> events)
    {
        if(!events.AllEventsDefined()) throw new Exception("Not all C4PGGPO events are defined, all of them MUST be defined. It's not a option.");
        if(stateNum  < 8) throw new Exception($"C4GGPO can't be initialized with less than 8 states! Tried to initialize with {stateNum} states.");
        if(bufferSize < 0) throw new Exception("Why the hell is the buffer size below 0???????????"); 

        EventCallbacks = events;
        SaveStates = new StateStorage<byte>(bufferSize, stateNum);
        InputHistory = new InputStorage<InputType>(100, stateNum);
        FutureInputs = new List<(int tickItWasPressed, int playerID, InputType input)>(100);

        Players = new InternalDictionary32<int, int>(100);
        RemovedPlayers = new List<int>(50);

        TickOffset = tick;
    }

    #region Public functions!

    public (RollbackErrors error, int id) Tick()
    {
        //Verify if the most delayed input will be out of range if this tick is processed.

        var mostDelayedInput = GetMostDelayedInput();

        if(
            mostDelayedInput.hasResult &&
            IsInputOutOfRange(mostDelayedInput.mostDelayedTick - 1) &&
            !(!IsInputOutOfRange(0) && mostDelayedInput.mostDelayedTick == 0 && InputHistory.Count != InputHistory.Capacity) &&
            mostDelayedInput.mostDelayedId != TickToProcess.responsibleId
        )
        {
            return (RollbackErrors.MOST_DELAYED_INPUT_OUT_OF_RANGE, mostDelayedInput.mostDelayedId);
        }

        ++PresentTick;

        InputHistory.MakeInputSlot();

        var newSlotDict = InputHistory.GetInputs(0);

        //Don't let removed player inputs sneak into next tick operation.
        if(RemovedPlayers.Count == 0) goto skipRemovePlayersOperation;

        for(int i = 0; i < RemovedPlayers.Count; ++i)
            newSlotDict.TryRemove(RemovedPlayers[i]);

        RemovedPlayers.Clear();

        skipRemovePlayersOperation:;
        
        //Add future inpúts operation
        for(int i = FutureInputs.Count - 1; i > -1; --i)
        {
            var curr = FutureInputs[i];

            if(curr.tickItWasPressed == PresentTick)
            {
                var error = InsertInput(curr.tickItWasPressed + TickOffset, curr.playerID, curr.input);
                if(error != RollbackErrors.NONE)
                {
                    //Catastrophic failure, this should NEVER happen.

                    throw new Exception($"Catastrophic unrecoverable failure of the c4pggpo instance due to bad input. ERROR: {error.ToString()} RESPONSIBLE: Player of id {curr.playerID} in tick {curr.tickItWasPressed}.");
                    //return (error, curr.playerID);
                }
                FutureInputs.RemoveAt(i);
            }
        }

        int tickToComeBack = int.MaxValue;

        if(TickToProcess.tick != int.MaxValue)
        {
            tickToComeBack = TickToProcess.tick - 1;

            //Error scrapped because it would never actually happen.
            /*if (IsInputOutOfRange(tickToComeBack) && !(!IsInputOutOfRange(0) && tickToComeBack == -1))
            {
                Console.WriteLine($"The most delayed input is out of range of this instances limit.");

                return (RollbackErrors.TICK_OUT_OF_RANGE, TickToProcess.responsibleId);
            }
            */
        }

        int lastFullTick = mostDelayedInput.mostDelayedTick;

        if(!mostDelayedInput.hasResult)
        {
            mostDelayedInput = GetMostDelayedInput();

            lastFullTick = mostDelayedInput.mostDelayedTick;

            if (!mostDelayedInput.hasResult) lastFullTick = -1;
        }

        if (tickToComeBack != int.MaxValue) ComeBackToTickMinus(tickToComeBack);

        while(CurrTick != PresentTick)
        {
            InternalTick(lastFullTick);
        }

        TickToProcess = (int.MinValue, int.MaxValue);

        return (RollbackErrors.NONE, int.MinValue);
    }

    /// <summary>
    /// Inserts input into the simulation.
    /// Following erros can ocurr while doing this:
    /// TICK_OUT_OF_RANGE, LONG_CONFIRMED_INPUT, INPUT_NONSEQUENTIAL, and of course NONE.
    /// </summary>
    /// <param name="tickItWasPressed"></param>
    /// <param name="playerID"></param>
    /// <param name="input"></param>
    /// <returns></returns>
    public RollbackErrors InsertInput(int tickItWasPressed, int playerID, InputType input)
    {
        tickItWasPressed -= TickOffset;

        if(tickItWasPressed > PresentTick)
        {
            if(tickItWasPressed - PresentTick > MaxInputAhead) return RollbackErrors.INPUT_IN_UNREACHABLE_FUTURE;

            int lastInp = int.MinValue;

            bool hadInput = Players.TryGetValue(playerID, out lastInp);

            for (int i = 0; i < FutureInputs.Count; ++i)
            {
                var curr = FutureInputs[i];
                if(curr.playerID == playerID && curr.tickItWasPressed > lastInp) lastInp = curr.tickItWasPressed;
            }

            if(lastInp == int.MinValue) goto futureTickSucess;

            if(hadInput && tickItWasPressed <= lastInp) return RollbackErrors.LONG_CONFIRMED_INPUT;

            if(hadInput && tickItWasPressed != lastInp + 1) return RollbackErrors.INPUT_NONSEQUENTIAL;

            futureTickSucess:
            FutureInputs.Add((tickItWasPressed, playerID, input));
            goto endend;
        }

        int lastSentInputTick;

        if(IsSaveOutOfRange(tickItWasPressed - 1) && !(tickItWasPressed == 0 && !IsSaveOutOfRange(0))) return RollbackErrors.TICK_OUT_OF_RANGE;

        if(Players.TryGetValue(playerID, out lastSentInputTick))
        {
            if(tickItWasPressed <= lastSentInputTick)
            {
                return RollbackErrors.LONG_CONFIRMED_INPUT;
            }

            if(tickItWasPressed != lastSentInputTick + 1)
            {
                EventCallbacks.PrintInConsole.Invoke($"C4PGGPO ERROR: Tried to insert a input in tick {tickItWasPressed} but last confirmed1 input was for tick {lastSentInputTick}, meaning all inputs between them were lost.");

                return RollbackErrors.INPUT_NONSEQUENTIAL;
            }

            Players[playerID] = tickItWasPressed;
        }
        else
        {
            Players.Add(playerID, tickItWasPressed);
        }

        InputHistory.PushInputToSlots(input, playerID, PresentTick - tickItWasPressed);

        if(TickToProcess.tick > tickItWasPressed) TickToProcess = (playerID, tickItWasPressed);

        endend:

        return RollbackErrors.NONE;
    }

    public void RemovePlayer(int id)
    {
        int lastInputTick;

        if(Players.TryGetValue(id, out lastInputTick))
        {
            Players.Remove(id);

            var mdi = GetMostDelayedInput();

            if (PresentTick > lastInputTick && TickToProcess.tick < lastInputTick + 1) TickToProcess = (id, lastInputTick + 1);

            for(int i = 0; i < FutureInputs.Count; ++i)
            {
                if(FutureInputs[i].playerID == id)
                {
                    FutureInputs.RemoveAt(i);
                    --i;
                }
            }

            if(lastInputTick < PresentTick) InputHistory.RemoveInputFromSlots(id, GetTickIndex(lastInputTick + 1));

            RemovedPlayers.Add(id);

            return;
        }

        throw new Exception("Player, in fact, did not exist.");
    }

    /// <summary>
    /// <para>Obtains the solver that can synchronize the world of two instances of C4PGGPO.</para>
    /// This synchronizer packet can be sent to all current players. It does not need to be generated multiple times.
    /// <para>PROBABLE ERROR: if you're sending this and a player NEVER had their input processed by the Tick of this instance, 
    /// it will bug out and all who receive this will have a bad world state.</para>
    /// </summary>
    public C4PWorldSynchronizer<InputType> GetWorldSynchronizer()
    {
        if(TickToProcess.tick != int.MaxValue)
            throw new Exception("Can't make concrete worldstate while processing inputs.");

        var mostDelayed = GetMostDelayedInput();

        if(!mostDelayed.hasResult) throw new Exception("Can't get world synchronizer with no players registered.");

        int tick = mostDelayed.mostDelayedTick;

        byte[] state = GetStateInternal(tick);

        (int playerId, InputType input)[] inputs = GetInputArrayInternal(tick);

        return new C4PWorldSynchronizer<InputType> (tick + TickOffset, state, inputs);
    }
    /// <summary>
    /// UNTESTED METHOD.
    /// </summary>
    /// <param name="synchronizer"></param>
    /// <returns></returns>
    public RollbackErrors SynchronizeWorld(C4PWorldSynchronizer<InputType> synchronizer)
    {
        int tick = synchronizer.Tick - TickOffset;

        if(tick > PresentTick) throw new Exception("Can't synchronize with a future state.");
        if(!(tick == PresentTick || (tick == PresentTick + 1 && PresentTick == - 1)) && IsInputOutOfRange(tick))
            return RollbackErrors.TICK_OUT_OF_RANGE;

        //EventCallbacks.Load(synchronizer.WorldState);

        //SaveStates.Clear();

        //SaveStates.StoreState(synchronizer.WorldState);

        var inputs = synchronizer.Inputs;

        if(InputHistory.Count == 0)
        {
            InputHistory.MakeInputSlot();
            for (int i = 0; i < inputs.Length; ++i)
            {
                var curr = inputs[i];

                InputHistory.PushInputToSlots(curr.input, curr.playerId, 0);

                Players.Add(curr.playerId, 0);
            }

            SaveStates.StoreState(synchronizer.WorldState);

            EventCallbacks.Load(synchronizer.WorldState);

            PresentTick = 0;
            CurrTick = 0;
        }
        else
        {
            int size = (PresentTick - synchronizer.Tick) + 1;
            while(InputHistory.Count > size)
            {
                InputHistory.ErasePast();
                SaveStates.ErasePast();
            }

            var history = InputHistory.GetInputs(GetTickIndex(tick));

            int tickIndex = GetTickIndex(tick);

            for (int i = 0; i < inputs.Length; ++i)
            {
                var curr = inputs[i];

                InputType registInput;
                if(history.TryGetValue(curr.playerId, out registInput))
                {
                    int lastPlayerInput = Players[curr.playerId];

                    if(lastPlayerInput < tick)
                    {
                        InputHistory.PushInputToSlots(curr.input, curr.playerId, tickIndex);
                        Players[curr.playerId] = tick;
                    }
                }
                else
                {
                    InputHistory.PushInputToSlots(curr.input, curr.playerId, tickIndex);

                    if(Players.ContainsKey(curr.playerId)) Players[curr.playerId] = tick;
                    else Players.Add(curr.playerId, tick);
                }
            }

            SaveStates.SetState(tickIndex, synchronizer.WorldState);

            if (tick == PresentTick)
            {
                EventCallbacks.Load(synchronizer.WorldState);
            }
            else TickToProcess = (int.MinValue, tick + 1);
        }

        return RollbackErrors.NONE;
    }

    #endregion

    #region Internal Functions

    private void InternalTick(int pastestInput)
    {
        int lastFullTick = pastestInput;

        var nextTickInput = InputHistory.GetInputs(GetTickIndex(CurrTick + 1));

        int sinceLastFullTick = CurrTick - lastFullTick;

        ++CurrTick;
        EventCallbacks.Tick.Invoke(lastFullTick > CurrTick? 0 : CurrTick - lastFullTick, nextTickInput);
        SaveStates.StoreState(EventCallbacks.Save);
    }

    private void InternalLoad(int tick)
    {
        int index = GetTickIndex(tick);

        EventCallbacks.Load.Invoke(SaveStates.GetState(index));
    }
    /// <summary>
    /// Come back to tick wich the SaveStates aren't caught up on in the present, and are still on the past present by -1.
    /// </summary>
    /// <param name="tick"></param>
    private void ComeBackToTickMinus(int tick)
    {
        
        if (tick == -1) SaveStates.ErasePast(SaveStates.Count - 1);
        else
        {
            SaveStates.ErasePast(GetTickIndex(tick + 2));
            Console.WriteLine(SaveStates.Count);
        }

        if(tick == -1) EventCallbacks.Load(EventCallbacks.GetDefaultWorldState());
        else EventCallbacks.Load(SaveStates.GetState(0));

        CurrTick = tick;
    }

    private void ComeBackToTick(int tick)
    {
        
        if (tick == -1) SaveStates.ErasePast(SaveStates.Count - 1);
        else
        {
            SaveStates.ErasePast(GetTickIndex(tick + 1));
            Console.WriteLine(SaveStates.Count);
        }

        if(tick == -1) EventCallbacks.Load(EventCallbacks.GetDefaultWorldState());
        else EventCallbacks.Load(SaveStates.GetState(0));

        CurrTick = tick;
    }

    private int GetTickIndex(int tick) => PresentTick - tick;

    private bool IsSaveOutOfRange(int tick)
    {
        Console.WriteLine($"Save {tick} {GetTickIndex(tick) + 1} > {SaveStates.Count}");

        return GetTickIndex(tick) + 1 > SaveStates.Count;
    }

    private bool IsInputOutOfRange(int tick)
    {
        Console.WriteLine($"Input {tick} {GetTickIndex(tick) + 1} > {InputHistory.Count}");

        return GetTickIndex(tick) + 1 > InputHistory.Count;
    }

    private (bool hasResult, int mostDelayedTick, int mostDelayedId) GetMostDelayedInput()
    {
        var pList = Players.GetInternalList();
        int playerID = int.MinValue;
        int lastFullTick = int.MaxValue;

        for(int i = 0; i < pList.Count; ++i)
        {
            var curr = pList[i];

            if(curr.Value < lastFullTick)
            {
                playerID = curr.Key;
                lastFullTick = curr.Value;
            }
        }

        return (lastFullTick != int.MaxValue, lastFullTick, playerID);
    }

    private byte[] GetStateInternal(int tick)
    {
        tick -= TickOffset;

        int index = GetTickIndex(tick);

        return SaveStates.GetState(index);
    }

    private (int id, InputType input)[] GetInputArrayInternal(int tick)
    {
        tick -= TickOffset;

        int index = GetTickIndex(tick);

        var inputs = InputHistory.GetInputs(tick).GetInternalList();

        var array = new (int id, InputType input)[inputs.Count];

        for(int i = 0; i<array.Length; ++i)
        {
            var curr = inputs[i];

            array[i] = (curr.Key, curr.Value);
        }

        return array;
    }

    #endregion

    bool Removed = false;

    public void Remove()
    {
        if(Removed) return;
        Removed = true;

        //EventCallbacks.Dispose();
        //SaveStates.Dispose();

        EventCallbacks = null;
        SaveStates = null;

        //this.Dispose();
    }

    //Essa é a classe que tem os eventos, ta vendo?
    public class CSGGPOEvents<T>
    {
        public Func<byte[], byte[]> Save = null;
        public Action<byte[]> Load = null;
        /// <summary>
        /// Argument 1: how many ticks since all players inputs were received.
        /// Argument 2: the dictionary of inputs this tick, by ID and input type.
        /// </summary>
        public Action<int, InternalDictionary32<int, T>> Tick = null;
        public Func<T> CreateDefaultInput = null;
        public Func<byte[]> GetDefaultWorldState = null;
        public Action<string> PrintInConsole;

        public bool AllEventsDefined()
        {
            return
            Save != null &&
            Load != null &&
            Tick != null &&
            CreateDefaultInput != null;
        }

        //Aqui tá o construtor.
        public CSGGPOEvents
        (
            Func<byte[], byte[]> save, Action<byte[]> load, Action<int, InternalDictionary32<int, T>> tick,
            Func<T> createDefaultInput, Func<byte[]> getDefaultWorldstate, Action<string> printInConsole
        )
        {
            Save = save;
            Load = load;
            Tick = tick;
            CreateDefaultInput = createDefaultInput;
            GetDefaultWorldState = getDefaultWorldstate;
            PrintInConsole = printInConsole;
        }

        public CSGGPOEvents()
        {

        }
    }

    private class StateStorage<T>
    {
        private T[][] States;
        private int NonFilledStates;

        public int Count
        {
            get => States.Length - NonFilledStates;
        }

        public int Capacity
        {
            get => States.Length;
        }

        /// <summary>
        /// Buffer size is the amount of bytes in each state, state num is the max range of
        /// states this can store.
        /// </summary>
        /// <param name="bufferSize"></param>
        /// <param name="stateNum"></param>
        public StateStorage(int bufferSize, int stateNum)
        {
            States = new T[stateNum][];
            for (int i = 0; i < stateNum; ++i)
            {
                States[i] = new T[bufferSize];
            }

            NonFilledStates = stateNum;
        }

        /// <summary>
        /// Stores a state by altering a array with the 'saveFunc' anonymous function.
        /// </summary>
        /// <param name="saveFunc"></param>
        public void StoreState(Func<T[], T[]> saveFunc)
        {
            if(NonFilledStates == 0)
            {
                var lastState = States[0];

                int firstIndex = States.Length-1;
                
                for(int i = 0; i<firstIndex; ++i)
                {
                    States[i] = States[i+1];
                }

                States[firstIndex] = saveFunc.Invoke(lastState);

                return;
            }

            int index = States.Length - NonFilledStates;

            States[index] = saveFunc.Invoke(States[index]);

            --NonFilledStates;
        }

        public void StoreState(T[] state)
        {
            if(NonFilledStates == 0)
            {
                var lastState = States[0];

                int firstIndex = States.Length-1;
                
                for(int i = 0; i<firstIndex; ++i)
                {
                    States[i] = States[i+1];
                }

                States[firstIndex] = state;

                return;
            }

            int index = States.Length - NonFilledStates;

            States[index] = state;

            --NonFilledStates;
        }

        /// <summary>
        /// Gets states by index.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public T[] GetState(int index)
        {
            return States[States.Length - 1 - index - NonFilledStates];
        }

        public void SetState(int index, T[] state)
        {
            States[States.Length - 1 - index - NonFilledStates] = state;
        }

        /// <summary>
        /// Erases past state from index 0 to the specified index.
        /// </summary>
        /// <param name="tillIndex"></param>
        public void ErasePast(int tillIndex)
        {
            int result = NonFilledStates + tillIndex + 1;

            if(result > States.Length) throw new Exception($"ErasePast: {tillIndex} {States.Length} {NonFilledStates} {NonFilledStates + tillIndex + 1}");

            NonFilledStates = result;
        }

        public void ErasePast()
        {
            NonFilledStates += 1;
        }

        public void Clear()
        {
            NonFilledStates = States.Length;
        }
    }

    private class InputStorage<T>
    {
        private InternalDictionary32<int, T>[] States;
        private int NonFilledStates;

        public int Count
        {
            get => States.Length - NonFilledStates;
        }

        public int Capacity
        {
            get => States.Length;
        }

        public InternalDictionary32<int, T> this[int index]
        {
            get => GetInputs(index);
        }

        /// <summary>
        /// Buffer size is the amount of bytes in each state, state num is the max range of
        /// states this can store.
        /// </summary>
        /// <param name="bufferSize"></param>
        /// <param name="stateNum"></param>
        public InputStorage(int bufferSize, int stateNum)
        {
            States = new InternalDictionary32<int, T>[stateNum];
            for (int i = 0; i < stateNum; ++i)
            {
                States[i] = new InternalDictionary32<int, T>(bufferSize);
            }

            NonFilledStates = stateNum;
        }

        public void MakeInputSlot()
        {
            if(NonFilledStates == 0)
            {
                var lastState = States[0];

                int firstIndex = States.Length-1;
                
                for(int i = 0; i<firstIndex; ++i)
                {
                    States[i] = States[i+1];
                }

                if (firstIndex != 0) CopyDictValue(States[firstIndex - 1], lastState);
                else lastState.Clear();

                States[firstIndex] = lastState;

                return;
            }

            int index = States.Length - NonFilledStates;

            if (NonFilledStates == States.Length) States[index].Clear();
            else CopyDictValue(States[index - 1], States[index]);

            --NonFilledStates;
        }

        /// <summary>
        /// Gets states by index.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public InternalDictionary32<int, T> GetInputs(int index)
        {
            return States[States.Length - 1 - index - NonFilledStates];
        }

        /// <summary>
        /// Overrides inputs since the index specified to the first index.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="inputId"></param>
        /// <param name="sinceIndex"></param>
        public void PushInputToSlots(T input, int inputId, int sinceIndex)
        {
            int ind = States.Length - sinceIndex - 1 - NonFilledStates;

            for (int i = ind; i< States.Length - NonFilledStates; ++i)
            {
                var dict = States[i];

                bool existed;
                dict.AddIfNonexist(inputId, input, out existed);
                
                if(existed) dict[inputId] = input;
            }
        }

        public void RemoveInputFromSlots(int inputId, int sinceIndex)
        {
            int ind = States.Length - sinceIndex - 1 - NonFilledStates;

            for (int i = ind; i< States.Length - NonFilledStates; ++i)
            {
                var dict = States[i];

                dict.TryRemove(inputId);
            }
        }


        /// <summary>
        /// If the inputs are not a sequence and have a GAP, it will return false.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="inputs"></param>
        /// <param name="inputId"></param>
        /// <returns></returns>
        public (bool result, int gapHigher, int gapLower) PushMultipleInputsToSlots((T input, int sinceIndex)[] inputs, int inputId)
        {
            if(inputs.Length == 0) return (false, -1, -1);

            int higherToLowerCount = 0;
            Span<int> higherToLower = stackalloc int[inputs.Length];

            int lastHighest = int.MaxValue;

            for(int i1 = 0; i1 < inputs.Length; ++i1)
            {
                int currHighest = -1;

                for(int i2 = 0; i2 < inputs.Length; ++i2)
                {
                    int sinceIndex = inputs[i2].sinceIndex;
                    if(sinceIndex > currHighest && sinceIndex < lastHighest) currHighest = i2;
                }

                //If it's not sequential, it's not processable, BAIL.
                //Operation failed.
                if(currHighest == -1 || currHighest != lastHighest - 1) return (false, lastHighest, currHighest);

                //Add value to the sequential index list AKA higherToLower
                //and register this as the last highest value.
                //Observation: extremely memory efficient.
                higherToLower[higherToLowerCount] = currHighest;
                ++higherToLowerCount;

                lastHighest = currHighest;
            }

            //Add the inputs.
            for(int i = 0; i < higherToLower.Length - 1; ++i)
            {
                var currInput = inputs[higherToLower[i]];

                int ind = States.Length - currInput.sinceIndex - 1 - NonFilledStates;

                var dict = States[ind];

                bool existed;
                dict.AddIfNonexist(inputId, currInput.input, out existed);
                
                if(existed) dict[inputId] = currInput.input;
            }

            var finalInput = inputs[higherToLower[higherToLower.Length - 1]];

            PushInputToSlots(finalInput.input, inputId, finalInput.sinceIndex);

            return (true, 0, 0);
        }

        public (bool result, int gapHigher, int gapLower) PushMultipleInputsToSlots(Span<(T input, int sinceIndex)> inputs, int inputId)
        {
            if(inputs.Length == 0) return (false, -1, -1);

            int higherToLowerCount = 0;
            Span<int> higherToLower = stackalloc int[inputs.Length];

            int lastHighest = int.MaxValue;

            for(int i1 = 0; i1 < inputs.Length; ++i1)
            {
                int currHighest = -1;

                for(int i2 = 0; i2 < inputs.Length; ++i2)
                {
                    int sinceIndex = inputs[i2].sinceIndex;
                    if(sinceIndex > currHighest && sinceIndex < lastHighest) currHighest = i2;
                }

                //If it's not sequential, it's not processable, BAIL.
                //Operation failed.
                if(currHighest == -1 || currHighest != lastHighest - 1) return (false, lastHighest, currHighest);

                //Add value to the sequential index list AKA higherToLower
                //and register this as the last highest value.
                //Observation: extremely memory efficient.
                higherToLower[higherToLowerCount] = currHighest;
                ++higherToLowerCount;

                lastHighest = currHighest;
            }

            //Add the inputs.
            for(int i = 0; i < higherToLower.Length - 1; ++i)
            {
                var currInput = inputs[higherToLower[i]];

                int ind = States.Length - currInput.sinceIndex - 1 - NonFilledStates;

                var dict = States[ind];

                bool existed;
                dict.AddIfNonexist(inputId, currInput.input, out existed);
                
                if(existed) dict[inputId] = currInput.input;
            }

            var finalInput = inputs[higherToLower[higherToLower.Length - 1]];

            PushInputToSlots(finalInput.input, inputId, finalInput.sinceIndex);

            return (true, 0, 0);
        }

        public void RemoveInput(int inputId, int index)
        {
            int ind = States.Length - index - 1 - NonFilledStates;

            for (int i = ind; i< States.Length - NonFilledStates; ++i)
            {
                var dict = States[i];

                dict.TryRemove(inputId);
            }
        }

        public void RemoveId(int inputId)
        {
            if (States.Length!= NonFilledStates) RemoveInput(inputId, States.Length - NonFilledStates - 1);
        }

        public void ErasePast()
        {
            NonFilledStates += 1;
        }

        /// <summary>
        /// Erases past state from index 0 to the specified index.
        /// </summary>
        /// <param name="tillIndex"></param>
        public void ErasePast(int tillIndex)
        {
            NonFilledStates += tillIndex + 1;
        }
        public void Clear()
        {
            NonFilledStates = States.Length;
        }

        //copies dick value
        private void CopyDictValue(InternalDictionary32<int, T> source, InternalDictionary32<int, T> destination)
        {
            destination.Clear();

            var internalList = source.GetInternalList();

            for (int i = 0; i < internalList.Count; ++i)
            {
                var curr = internalList[i];

                destination.Add(curr.Key, curr.Value);
            }
        }
    }
}

public class C4PWorldSynchronizer<InputType>
{
    public int Tick;
    public byte[] WorldState;
    public (int playerId, InputType input)[] Inputs;

    public C4PWorldSynchronizer(int tick, byte[] worldState, (int playerId, InputType input)[] inputs)
    {
        Tick = tick;
        WorldState = worldState;
        Inputs = inputs;
    }
}

public enum RollbackErrors
{
    ///<summary>
    /// No error, AKA: it's just safe to go.
    ///</summary>
    NONE,
    ///<summary>
    /// You tried to insert a input that was already comfirmed before.
    ///</summary>
    LONG_CONFIRMED_INPUT,
    ///<summary>
    /// You forgot or lost one or more inputs between the last confirmed input and the one you're trying to insert now.
    ///</summary>
    INPUT_NONSEQUENTIAL,
    ///<summary>
    /// You forgot or lost one or more inputs between 2 inputs in the list.
    ///</summary>
    INPUT_LIST_NONSEQUENTIAL,
    ///<summary>
    /// The tick you're trying to alter was already forgotten by the capacity you defined for this C4PGGPO instance.
    ///</summary>
    TICK_OUT_OF_RANGE,
    ///<summary>
    /// A input is so delayed, execution just wasn't possible.
    ///</summary>
    MOST_DELAYED_INPUT_OUT_OF_RANGE,
    ///<summary>
    /// The input is so in the future, it violates the instance's limit.
    ///</summary>
    INPUT_IN_UNREACHABLE_FUTURE,
}
